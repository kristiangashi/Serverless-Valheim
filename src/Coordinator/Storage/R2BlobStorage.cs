using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;

namespace Coordinator.Storage;

/// <summary>
/// Stores world archives in Cloudflare R2 via its S3-compatible API. This is the durable,
/// production store — unlike Railway's ephemeral container disk, R2 survives redeploys.
/// </summary>
public sealed partial class R2BlobStorage : IBlobStorage, IPresignedBlobStorage
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _prefix;

    /// <param name="keyPrefix">
    /// Optional namespace within the bucket (e.g. "staging/"). Empty means the bucket root, which
    /// is what production uses. A prefixed instance can't see or touch keys outside its namespace,
    /// so a second coordinator can share one bucket without any risk to the live world.
    /// </param>
    public R2BlobStorage(
        string accountId, string accessKeyId, string secretAccessKey, string bucket, string keyPrefix = "")
    {
        _bucket = bucket;
        _prefix = keyPrefix.Length == 0 || keyPrefix.EndsWith('/') ? keyPrefix : keyPrefix + "/";
        var config = new AmazonS3Config
        {
            ServiceURL = $"https://{accountId}.r2.cloudflarestorage.com",
            ForcePathStyle = true,        // R2 wants path-style addressing
            AuthenticationRegion = "auto", // R2 ignores region but the SDK requires one
            // AWS SDK v4 defaults to streaming uploads with trailing checksums
            // (STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER), which R2 doesn't implement.
            // Only add checksums when an operation actually requires them.
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
        };
        _s3 = new AmazonS3Client(accessKeyId, secretAccessKey, config);
    }

    private string KeyFor(int version) => $"{_prefix}world-v{version}.zip";

    public async Task SaveAsync(int version, Stream content, CancellationToken ct = default)
    {
        // DisablePayloadSigning makes the SDK send x-amz-content-sha256: UNSIGNED-PAYLOAD instead of
        // the chunked STREAMING-AWS4-HMAC-SHA256-PAYLOAD encoding, which R2 doesn't implement.
        // The form file is buffered to a seekable stream by ASP.NET, so a single PUT is fine
        // (R2 accepts single objects up to ~5 GB — far above any Valheim world).
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = KeyFor(version),
            InputStream = content,
            ContentType = "application/zip",
            DisablePayloadSigning = true,
            AutoCloseStream = false,
        }, ct);
    }

    // Staged uploads live under a prefix the version scan never looks at, so an abandoned one can
    // never be mistaken for the world. Swept up by PruneAsync once it's too old to still be in flight.
    private static readonly TimeSpan PendingTtl = TimeSpan.FromHours(1);

    private string PendingPrefix => $"{_prefix}pending/";
    private string PendingKeyFor(string uploadId) => $"{PendingPrefix}{uploadId}.zip";

    public string PutContentType => "application/zip";

    public Task<string> PresignPutAsync(string uploadId, TimeSpan lifetime, CancellationToken ct = default) =>
        _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = PendingKeyFor(uploadId),
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(lifetime),
            ContentType = PutContentType,
        });

    public async Task PromoteAsync(string uploadId, int version, CancellationToken ct = default)
    {
        // Server-side copy: R2 moves the object internally, so none of it crosses this service.
        try
        {
            await _s3.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = PendingKeyFor(uploadId),
                DestinationBucket = _bucket,
                DestinationKey = KeyFor(version),
            }, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "The uploaded world is no longer in staging — it may have expired. Please upload again.");
        }
        // Best effort: a leftover staging object is harmless and PruneAsync sweeps it later.
        try { await _s3.DeleteObjectAsync(_bucket, PendingKeyFor(uploadId), ct); } catch { }
    }

    public Task<string> PresignGetAsync(int version, TimeSpan lifetime, CancellationToken ct = default) =>
        _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = KeyFor(version),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
        });

    public async Task<Stream?> OpenAsync(int version, CancellationToken ct = default)
    {
        try
        {
            var resp = await _s3.GetObjectAsync(_bucket, KeyFor(version), ct);
            return resp.ResponseStream; // caller disposes; that releases the underlying HTTP stream
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task PruneAsync(int currentVersion, int keep, CancellationToken ct = default)
    {
        foreach (var (key, v, _) in await ListVersionsAsync(ct))
        {
            if (v <= currentVersion - keep)
            {
                try { await _s3.DeleteObjectAsync(_bucket, key, ct); } catch { /* best effort */ }
            }
        }
        await SweepPendingAsync(ct);
    }

    // Drop staged uploads too old to still be in flight — clients that died mid-PUT, or that never
    // came back to commit. The TTL sits well beyond the presigned URL's lifetime so a slow upload
    // is never pulled out from under someone.
    private async Task SweepPendingAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - PendingTtl;
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = PendingPrefix };
        ListObjectsV2Response resp;
        do
        {
            resp = await _s3.ListObjectsV2Async(request, ct);
            foreach (var obj in resp.S3Objects ?? [])
            {
                if (obj.LastModified is { } modified && modified < cutoff)
                {
                    try { await _s3.DeleteObjectAsync(_bucket, obj.Key, ct); } catch { /* best effort */ }
                }
            }
            request.ContinuationToken = resp.NextContinuationToken;
        } while (resp.IsTruncated == true);
    }

    public async Task<(int Version, DateTimeOffset? UpdatedAt)> GetLatestAsync(CancellationToken ct = default)
    {
        var latest = 0;
        DateTimeOffset? updated = null;
        foreach (var (_, v, modified) in await ListVersionsAsync(ct))
            if (v > latest) { latest = v; updated = modified; }
        return (latest, updated);
    }

    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        foreach (var (key, _, _) in await ListVersionsAsync(ct))
        {
            try { await _s3.DeleteObjectAsync(_bucket, key, ct); } catch { /* best effort */ }
        }
        // Staged uploads too, or an in-flight one could land after the reset and resurrect the world.
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = PendingPrefix };
        ListObjectsV2Response resp;
        do
        {
            resp = await _s3.ListObjectsV2Async(request, ct);
            foreach (var obj in resp.S3Objects ?? [])
            {
                try { await _s3.DeleteObjectAsync(_bucket, obj.Key, ct); } catch { /* best effort */ }
            }
            request.ContinuationToken = resp.NextContinuationToken;
        } while (resp.IsTruncated == true);
    }

    private async Task<List<(string Key, int Version, DateTimeOffset? Modified)>> ListVersionsAsync(CancellationToken ct)
    {
        var results = new List<(string, int, DateTimeOffset?)>();
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = $"{_prefix}world-v" };
        ListObjectsV2Response resp;
        do
        {
            resp = await _s3.ListObjectsV2Async(request, ct);
            // AWS SDK v4 returns null (not an empty list) when the bucket/prefix has no objects.
            foreach (var obj in resp.S3Objects ?? [])
            {
                // The regex describes the name, so match past our namespace rather than baking the
                // prefix into it — otherwise a prefixed store never recognises its own versions.
                var m = VersionRegex().Match(obj.Key[_prefix.Length..]);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var v))
                {
                    DateTimeOffset? modified = obj.LastModified is { } dt ? dt : null;
                    results.Add((obj.Key, v, modified));
                }
            }
            request.ContinuationToken = resp.NextContinuationToken;
        } while (resp.IsTruncated == true);
        return results;
    }

    [GeneratedRegex(@"^world-v(\d+)\.zip$")]
    private static partial Regex VersionRegex();
}
