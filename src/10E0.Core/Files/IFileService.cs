namespace TenE0.Core.Files;

/// <summary>
/// 文件服务接口
/// </summary>
public interface IFileService
{
    /// <summary>
    /// 上传文件
    /// </summary>
    Task<UploadResponse> UploadAsync(Stream stream, string fileName, string contentType, UploadRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// 上传图片（带处理选项）
    /// </summary>
    Task<UploadResponse> UploadImageAsync(Stream stream, string fileName, ImageProcessOptions? options = null, UploadRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// 下载文件
    /// </summary>
    Task<(Stream? Stream, TenE0FileAttachment? Metadata)> DownloadAsync(string fileId, CancellationToken ct = default);

    /// <summary>
    /// 删除文件
    /// </summary>
    /// <remarks>
    /// 取代旧的 <c>Task&lt;bool&gt;</c> 签名：旧 API 把 storage 删除失败和"文件不存在"
    /// 都映射成单一 bool，调用方无法区分（PR #6 早期实现 + issue #9 Part 2）。
    /// 新签名返回 <see cref="DeleteResult"/>，明确拆出
    /// <see cref="DeleteResult.MetadataDeleted"/>（元数据软删除是否成功）和
    /// <see cref="DeleteResult.StorageDeleted"/>（物理文件是否成功删除），
    /// 让调用方在 storage 失败时主动重试或上报。
    /// </remarks>
    Task<DeleteResult> DeleteAsync(string fileId, CancellationToken ct = default);

    /// <summary>
    /// 获取文件元数据
    /// </summary>
    Task<TenE0FileAttachment?> GetMetadataAsync(string fileId, CancellationToken ct = default);

    /// <summary>
    /// 获取文件的访问 URL
    /// </summary>
    Task<string?> GetAccessUrlAsync(string fileId, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IFileService.DeleteAsync"/> 的结果记录，区分两种"删除成功"语义。
/// <para>
/// 软删除一条文件记录会触发两个副作用：
/// </para>
/// <list type="number">
///   <item><b>元数据软删除</b>：把 <c>TenE0FileAttachment.IsDeleted</c> 置 true 并落库；
///       之后任何 <see cref="IFileService.GetMetadataAsync"/> / <see cref="IFileService.DownloadAsync"/>
///       调用都看不到该文件。</item>
///   <item><b>物理文件删除</b>：调用 <c>IFileStorage.DeleteAsync</c> 删除云存储里的对象
///       （主文件 + 缩略图）。这一步可能因为网络抖动、凭据失效、并发删除等原因失败。</item>
/// </list>
/// <para>
/// 两个步骤独立报告：
/// </para>
/// <list type="bullet">
///   <item><see cref="MetadataDeleted"/> = <c>false</c>：文件根本不存在 / 已软删除，
///       <b>无操作发生</b>，调用方应视作 <c>404</c>。</item>
///   <item><see cref="StorageDeleted"/> = <c>false</c>：元数据已软删除成功，但物理文件
///       至少有一个删除失败（主文件或缩略图）；调用方应记 warning 并考虑
///       后台清理 / 重试，因为对象存储里残留的孤儿文件会产生存储费。</item>
/// </list>
/// </summary>
/// <param name="MetadataDeleted">元数据软删除是否成功。<c>false</c> 表示文件不存在
///     或已删除（<c>IsDeleted == true</c>），无副作用发生。</param>
/// <param name="StorageDeleted">物理文件（主对象 + 缩略图）删除是否全部成功。
///     仅当 <paramref name="MetadataDeleted"/> = <c>true</c> 时才有意义。</param>
public sealed record DeleteResult(bool MetadataDeleted, bool StorageDeleted);
