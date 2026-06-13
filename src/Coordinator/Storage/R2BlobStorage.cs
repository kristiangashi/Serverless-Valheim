using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;

namespace Coordinator.Storage;

/// <summary>
/// Stores world archives in Cloudflare R2 via its S3-compatible API. This is the durable,
/// production store — unlike Railway's ephemeral container disk, R2 survives redeploys.
/// </summary>
public sealed partial class R2BlobStorage : IBlobStorage
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public R2BlobStorage(string accountId, string accessKeyId, string secretAccessKey, string bucket)
    {
        _bucket = bucket;
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

    private static string KeyFor(int version) => $"world-v{version}.zip";

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
        foreach (var (key, v) in await ListVersionsAsync(ct))
        {
            if (v <= currentVersion - keep)
            {
                try { await _s3.DeleteObjectAsync(_bucket, key, ct); } catch { /* best effort */ }
            }
        }
    }

    public async Task<int> GetLatestVersionAsync(CancellationToken ct = default)
    {
        var latest = 0;
        foreach (var (_, v) in await ListVersionsAsync(ct))
            if (v > latest) latest = v;
        return latest;
    }

    private async Task<List<(string Key, int Version)>> ListVersionsAsync(CancellationToken ct)
    {
        var results = new List<(string, int)>();
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = "world-v" };
        ListObjectsV2Response resp;
        do
        {
            resp = await _s3.ListObjectsV2Async(request, ct);
            foreach (var obj in resp.S3Objects)
            {
                var m = VersionRegex().Match(obj.Key);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var v)) results.Add((obj.Key, v));
            }
            request.ContinuationToken = resp.NextContinuationToken;
        } while (resp.IsTruncated == true);
        return results;
    }

    [GeneratedRegex(@"^world-v(\d+)\.zip$")]
    private static partial Regex VersionRegex();
}
