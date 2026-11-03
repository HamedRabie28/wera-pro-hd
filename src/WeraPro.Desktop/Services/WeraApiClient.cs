// Services/WeraApiClient.cs — WERA PRO HD Desktop (G3)
// العميل الوحيد للتعامل مع API: IHttpClientFactory + معرف ترابط لكل طلب +
// إرفاق رمز الوصول آلياً — كل النداءات async (لا تجميد UI §11.1).
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WeraPro.Contracts.V1;

namespace WeraPro.Desktop.Services;

/// <summary>
/// معالج يضيف معرف الترابط وترويسة المصادقة لكل طلب صادر —
/// نفس X-Correlation-ID الذي يقرؤه الخادم ويوصله حتى audit_logs (§11.1/§20).
/// </summary>
public sealed class CorrelationHandler(SessionService session) : DelegatingHandler
{
    private static readonly string HeaderName = "X-Correlation-ID";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var correlationId = "wera-" + Guid.NewGuid().ToString("N")[..20];
        request.Headers.Remove(HeaderName);
        request.Headers.Add(HeaderName, correlationId);

        if (session.AccessToken is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, ct);
        // 401 عند انتهاء صلاحية الوصول: محاولة تجديد شفافة واحدة ثم إعادة الطلب
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
            session.CanRefresh &&
            request.Headers.Authorization is not null &&
            !request.RequestUri!.AbsolutePath.EndsWith("/auth/refresh", StringComparison.OrdinalIgnoreCase) &&
            !request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            var refreshed = await session.TryRefreshAsync(ct);
            if (refreshed)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                response.Dispose();
                response = await base.SendAsync(request, ct);
            }
        }
        return response;
    }
}

/// <summary>
/// عميل API المكتوب (typed): يبني على HttpClient باسم مسجل في IHttpClientFactory.
/// كل الاستجابات تُفحص وتُترجم أخطاؤها إلى رسائل عربية واضحة — لا استثناء خام للواجهة.
/// </summary>
public sealed class WeraApiClient(
    IHttpClientFactory factory,
    SessionService session,
    ILogger<WeraApiClient> logger)
{
    public const string HttpClientName = "wera-api";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient CreateClient() => factory.CreateClient(HttpClientName);

    /// <summary>فحص جاهزية الخادم — يغذي حالة الاتصال الحقيقية (§10.6 لا «متصل» زوراً).</summary>
    public async Task<bool> IsServerReadyAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync("health/ready", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "health/ready غير متاح");
            return false;
        }
    }

    public async Task<ApiCallResult<LoginResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        return await PostAsync<LoginRequest, LoginResponse>(ApiRoutes.Login, request, allowAnonymous: true, ct);
    }

    public async Task<ApiCallResult<RefreshResponse>> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var request = new RefreshRequest(refreshToken);
        return await PostAsync<RefreshRequest, RefreshResponse>(ApiRoutes.Refresh, request, allowAnonymous: true, ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            using var _ = await client.PostAsJsonAsync(ApiRoutes.Logout, new { }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "تعذر إبلاغ الخادم بالخروج — الجلسة ستُغلق خادمياً بانتهاء الرمز");
        }
    }

    public async Task<ApiCallResult<List<BranchDto>>> GetBranchesAsync(CancellationToken ct = default)
    {
        var result = await GetAsync<List<BranchDto>>(ApiRoutes.Branches, ct);
        return result.Map(v => v ?? []);
    }

    public async Task<ApiCallResult<List<StationDto>>> GetStationsAsync(long branchId, CancellationToken ct = default)
    {
        return await GetAsync<List<StationDto>>(
            ApiRoutes.Stations.Replace("{branchId:long}", branchId.ToString(), StringComparison.Ordinal), ct);
    }

    public async Task<ApiCallResult<List<SettingDto>>> GetSettingsAsync(string? category, CancellationToken ct = default)
    {
        var url = ApiRoutes.Settings + (string.IsNullOrWhiteSpace(category) ? "" : "?category=" + Uri.EscapeDataString(category));
        var result = await GetAsync<List<SettingDto>>(url, ct);
        return result.Map(v => v ?? []);
    }

    // ── الكتالوج (G4) ─────────────────────────────────────────────────────

    /// <summary>بحث نصي محدود بترقيم خادمي (جدول الكتالوج المتقدم §7.2.1).</summary>
    public async Task<ApiCallResult<CatalogSearchResponse>> SearchProductsAsync(
        string? query, string? saleMode, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var qs = new List<string>
        {
            "page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(query))
        {
            qs.Insert(0, "query=" + Uri.EscapeDataString(query.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(saleMode))
        {
            qs.Add("saleMode=" + Uri.EscapeDataString(saleMode));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            qs.Add("status=" + Uri.EscapeDataString(status));
        }
        return await GetAsync<CatalogSearchResponse>(ApiRoutes.CatalogProducts + "?" + string.Join("&", qs), ct);
    }

    /// <summary>مطابقة دقيقة بالباركود/SKU/PLU (ماسح POS — §3.1).</summary>
    public async Task<ApiCallResult<CatalogLookupResponse>> LookupByCodeAsync(string code, CancellationToken ct = default)
    {
        var url = ApiRoutes.CatalogLookup + "?code=" + Uri.EscapeDataString(code.Trim());
        var result = await GetAsync<CatalogLookupResponse>(url, ct);
        return result;
    }

    public async Task<ApiCallResult<ProductDto>> GetProductAsync(long productId, CancellationToken ct = default)
    {
        var url = ApiRoutes.CatalogProductById.Replace(
            "{productId:long}", productId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return await GetAsync<ProductDto>(url, ct);
    }

    /// <summary>إنشاء منتج متعدد الأقسام — معاملة واحدة ذرية (لا حفظ جزئي §4.4).</summary>
    public async Task<ApiCallResult<ProductDto>> CreateProductAsync(CreateProductRequest request, CancellationToken ct = default)
    {
        return await PostAsync<CreateProductRequest, ProductDto>(ApiRoutes.CatalogProducts, request, allowAnonymous: false, ct);
    }

    public async Task<ApiCallResult<UnitsResponse>> GetUnitsAsync(CancellationToken ct = default)
    {
        return await GetAsync<UnitsResponse>(ApiRoutes.CatalogUnits, ct);
    }

    /// <summary>مرجع المحرر: الفئات والعلامات والقوائم السعرية النشطة.</summary>
    public async Task<ApiCallResult<CatalogReferenceResponse>> GetCatalogReferenceAsync(CancellationToken ct = default)
    {
        return await GetAsync<CatalogReferenceResponse>(ApiRoutes.CatalogReference, ct);
    }

    // ── مكتبة الوسائط (G5 — §6) ──────────────────────────────────────────

    /// <summary>بحث المكتبة بترقيم خادمي — بادئة البصمة/النوع/حالة الربط.</summary>
    public async Task<ApiCallResult<MediaSearchResponse>> SearchMediaAsync(
        string? sha, string? mime, string? linkState, int page, int pageSize, CancellationToken ct = default)
    {
        var qs = new List<string>
        {
            "page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(sha))
        {
            qs.Add("sha=" + Uri.EscapeDataString(sha.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(mime))
        {
            qs.Add("mime=" + Uri.EscapeDataString(mime));
        }
        if (!string.IsNullOrWhiteSpace(linkState))
        {
            qs.Add("linkState=" + Uri.EscapeDataString(linkState));
        }
        return await GetAsync<MediaSearchResponse>(ApiRoutes.Media + "?" + string.Join("&", qs), ct);
    }

    public async Task<ApiCallResult<MediaDetailsResponse>> GetMediaDetailsAsync(long mediaId, CancellationToken ct = default)
    {
        return await GetAsync<MediaDetailsResponse>(MediaById(mediaId), ct);
    }

    /// <summary>بايتات نسخة (original/thumb-catalog/thumb-pos/preview) — تستخدمها المصغرات والمعاينة.</summary>
    public async Task<MediaVariantFetch> GetMediaVariantAsync(long mediaId, string variant, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(MediaById(mediaId) + "/content/" + variant, ct);
            if (!response.IsSuccessStatusCode)
            {
                return MediaVariantFetch.Fail(await ReadErrorAsync(response, ct));
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return MediaVariantFetch.Ok(bytes, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
        }
        catch (TaskCanceledException)
        {
            return MediaVariantFetch.Fail("انتهت مهلة الطلب — تحقق من حالة الخادم");
        }
        catch (HttpRequestException)
        {
            return MediaVariantFetch.Fail("تعذر الوصول إلى الخادم — تحقق من العنوان والاتصال");
        }
    }

    /// <summary>رفع صورة واحدة — النتيجة تشير للموجود عند التطابق التام (409).</summary>
    public async Task<ApiCallResult<UploadMediaResponse>> UploadMediaAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            await using var source = File.OpenRead(filePath);
            using var owned = new MemoryStream();
            await source.CopyToAsync(owned, 64 * 1024, ct);
            owned.Position = 0;
            return await UploadMediaAsync(Path.GetFileName(filePath), owned, ct);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "تعذر قراءة ملف الرفع {Path}", filePath);
            return ApiCallResult<UploadMediaResponse>.Fail("تعذر قراءة الملف من القرص");
        }
    }

    /// <summary>رفع بايتات صورة باسم عرض من العميل (client_name للعرض فقط §6.1).</summary>
    public async Task<ApiCallResult<UploadMediaResponse>> UploadMediaAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            using var form = new MultipartFormDataContent();
            var part = new StreamContent(content);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "file", fileName);
            using var response = await client.PostAsync(ApiRoutes.Media, form, ct);
            return await ReadAsync<UploadMediaResponse>(response, ct);
        }
        catch (TaskCanceledException)
        {
            return ApiCallResult<UploadMediaResponse>.Fail("انتهت مهلة الطلب — تحقق من حالة الخادم");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "فشل رفع الوسائط");
            return ApiCallResult<UploadMediaResponse>.Fail("تعذر الوصول إلى الخادم — تحقق من العنوان والاتصال");
        }
    }

    public async Task<ApiCallResult<MediaLinkDto>> LinkMediaAsync(
        long mediaId, long? productId, long? skuId, bool makePrimary, CancellationToken ct = default)
    {
        return await PostAsync<LinkMediaRequest, MediaLinkDto>(
            MediaById(mediaId) + "/links",
            new LinkMediaRequest(productId, skuId, makePrimary), allowAnonymous: false, ct);
    }

    /// <summary>فك ربط — لا يمس الأصل أبداً؛ يعيد عدد الروابط المتبقية للأصل.</summary>
    public async Task<ApiCallResult<int>> UnlinkMediaAsync(long linkId, CancellationToken ct = default)
    {
        var result = await DeleteAsync<UnlinkResponse>(ApiRoutes.Media + "/links/" + linkId, ct);
        return result.Map(r => r.RemainingLinksForAsset);
    }

    public async Task<ApiCallResult<MediaLinkDto>> SetPrimaryLinkAsync(long linkId, CancellationToken ct = default)
    {
        return await PostWithResponseAsync<MediaLinkDto>(
            ApiRoutes.Media + "/links/" + linkId + "/set-primary", ct);
    }

    /// <summary>
    /// حذف أصل — يُرفض خادمياً ما دام مربوطاً (409 يحمل الروابط = المراجعة)؛
    /// الرسالة تعرضها الواجهة والروابط تُجلب بتفاصيل الأصل عند الحاجة.
    /// </summary>
    public async Task<ApiCallResult<long>> DeleteMediaAsync(long mediaId, CancellationToken ct = default)
    {
        var result = await DeleteAsync<IgnoreBody>(MediaById(mediaId), ct);
        return result.Map(_ => mediaId);
    }

    public async Task<ApiCallResult<MediaDto>> ReprocessThumbnailsAsync(long mediaId, CancellationToken ct = default)
    {
        return await PostWithResponseAsync<MediaDto>(MediaById(mediaId) + "/reprocess-thumbnails", ct);
    }

    /// <summary>تقرير المكررات — عرض فقط (لا دمج/حذف تلقائي §6.2).</summary>
    public async Task<ApiCallResult<MediaDuplicatesReportResponse>> GetDuplicatesReportAsync(
        int page = 1, CancellationToken ct = default)
    {
        return await GetAsync<MediaDuplicatesReportResponse>(
            ApiRoutes.Media + "/duplicates?page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
    }

    public async Task<ApiCallResult<SkuDefaultImageResponse>> GetSkuDefaultImageAsync(long skuId, CancellationToken ct = default)
    {
        var url = ApiRoutes.SkuDefaultImage.Replace(
            "{skuId:long}", skuId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return await GetAsync<SkuDefaultImageResponse>(url, ct);
    }

    // ── استيراد الصور الدفعي (G5 — §6.1) ────────────────────────────────

    public async Task<ApiCallResult<ImportJobDto>> CreateImportJobAsync(string label, CancellationToken ct = default)
    {
        return await PostAsync<CreateImportJobRequest, ImportJobDto>(
            ApiRoutes.MediaImportJobs, new CreateImportJobRequest(label), allowAnonymous: false, ct);
    }

    public async Task<ApiCallResult<ImportJobDto>> GetImportJobAsync(long jobId, CancellationToken ct = default)
    {
        return await GetAsync<ImportJobDto>(ImportJobById(jobId), ct);
    }

    public async Task<ApiCallResult<ImportJobDto>> StartImportJobAsync(long jobId, CancellationToken ct = default)
    {
        return await PostWithResponseAsync<ImportJobDto>(ImportJobById(jobId) + "/start", ct);
    }

    public async Task<ApiCallResult<ImportJobDto>> CancelImportJobAsync(long jobId, CancellationToken ct = default)
    {
        return await PostWithResponseAsync<ImportJobDto>(ImportJobById(jobId) + "/cancel", ct);
    }

    /// <summary>ترحيل ملفات إلى staging المهمة — multipart متعدد (client_name للعرض فقط).</summary>
    public async Task<ApiCallResult<ImportAddFilesResponse>> AddImportFilesAsync(
        long jobId, IReadOnlyList<(string FileName, Stream Content)> files, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            using var form = new MultipartFormDataContent();
            foreach (var (name, content) in files)
            {
                var part = new StreamContent(content);
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(part, "files", name);
            }
            using var response = await client.PostAsync(ImportJobById(jobId) + "/files", form, ct);
            return await ReadAsync<ImportAddFilesResponse>(response, ct);
        }
        catch (TaskCanceledException)
        {
            return ApiCallResult<ImportAddFilesResponse>.Fail("انتهت مهلة الطلب — تحقق من حالة الخادم");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "فشل ترحيل ملفات الاستيراد");
            return ApiCallResult<ImportAddFilesResponse>.Fail("تعذر الوصول إلى الخادم — تحقق من العنوان والاتصال");
        }
    }

    public async Task<ApiCallResult<ImportFilesPageResponse>> GetImportFilesAsync(
        long jobId, string? state, int page, int pageSize, CancellationToken ct = default)
    {
        var qs = "page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(state))
        {
            qs += "&state=" + Uri.EscapeDataString(state);
        }
        return await GetAsync<ImportFilesPageResponse>(ImportJobById(jobId) + "/files?" + qs, ct);
    }

    public async Task<ApiCallResult<ImportReportResponse>> GetImportReportAsync(long jobId, CancellationToken ct = default)
    {
        return await GetAsync<ImportReportResponse>(ImportJobById(jobId) + "/report", ct);
    }

    private static string MediaById(long mediaId) =>
        ApiRoutes.Media + "/" + mediaId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string ImportJobById(long jobId) =>
        ApiRoutes.MediaImportJobs + "/" + jobId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>DELETE عام — 204 بلا جسد أو JSON عند الرفض (رسالة عربية).</summary>
    private async Task<ApiCallResult<T>> DeleteAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.DeleteAsync(url, ct);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is 0 or null)
            {
                return ApiCallResult<T>.Ok(default!);
            }
            return await ReadAsync<T>(response, ct);
        }
        catch (TaskCanceledException)
        {
            return ApiCallResult<T>.Fail("انتهت مهلة الطلب — تحقق من حالة الخادم");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "فشل اتصال بـ{Url}", url);
            return ApiCallResult<T>.Fail("تعذر الوصول إلى الخادم — تحقق من العنوان والاتصال");
        }
    }

    /// <summary>POST بلا جسد يقرأ استجابة JSON (start/cancel/set-primary).</summary>
    private async Task<ApiCallResult<T>> PostWithResponseAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.PostAsync(url, content: null, ct);
            return await ReadAsync<T>(response, ct);
        }
        catch (TaskCanceledException)
        {
            return ApiCallResult<T>.Fail("انتهت مهلة الطلب — تحقق من حالة الخادم");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "فشل اتصال بـ{Url}", url);
            return ApiCallResult<T>.Fail("تعذر الوصول إلى الخادم — تحقق من العنوان والاتصال");
        }
    }

    /// <summary>قراءة رسالة الخطأ العربية من ProblemDetails (بلا 500 خام للواجهة).</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var detail = "تعذر إكمال الطلب";
        try
        {
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
            {
                var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(Json, ct);
                if (problem is not null)
                {
                    detail = problem.Detail ?? problem.Title ?? detail;
                }
            }
        }
        catch (JsonException)
        {
            // نص غير JSON — نكتفي بالرسالة العامة
        }
        return detail;
    }

    private async Task<ApiCallResult<T>> GetAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(url, ct);
            return await ReadAsync<T>(response, ct);
        }
        catch (TaskCanceledException)
        {
            return ApiCallResult<T>.Fail("انتهت مهلة الطلب — تحقق من حالة الخادم");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "فشل اتصال بـ{Url}", url);
            return ApiCallResult<T>.Fail("تعذر الوصول إلى الخادم — تحقق من العنوان والاتصال");
        }
    }

    private async Task<ApiCallResult<TRes>> PostAsync<TReq, TRes>(string url, TReq body, bool allowAnonymous, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.PostAsJsonAsync(url, body, ct);
            return await ReadAsync<TRes>(response, ct);
        }
        catch (TaskCanceledException)
        {
            return ApiCallResult<TRes>.Fail("انتهت مهلة الطلب — تحقق من حالة الخادم");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "فشل اتصال بـ{Url}", url);
            return ApiCallResult<TRes>.Fail("تعذر الوصول إلى الخادم — تحقق من العنوان والاتصال");
        }
    }

    private static async Task<ApiCallResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadFromJsonAsync<T>(Json, ct);
            return ApiCallResult<T>.Ok(payload!);
        }

        string detail = "تعذر إكمال الطلب";
        try
        {
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
            {
                var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(Json, ct);
                if (problem is not null)
                {
                    detail = problem.Detail ?? problem.Title ?? detail;
                }
            }
        }
        catch (JsonException)
        {
            // نص غير JSON — نكتفي بالرسالة العامة
        }
        return ApiCallResult<T>.Fail(detail);
    }
}

/// <summary>نتيجة نداء API للعرض في الواجهة — نجاح بقيمة أو فشل برسالة عربية.</summary>
public sealed record ApiCallResult<T>(bool Ok, T? Value, string? ErrorAr)
{
    public static ApiCallResult<T> Ok(T value) => new(true, value, null);
    public static ApiCallResult<T> Fail(string errorAr) => new(false, default, errorAr);

    public ApiCallResult<TOut> Map<TOut>(Func<T, TOut> map) =>
        Ok ? ApiCallResult<TOut>.Ok(map(Value!)) : ApiCallResult<TOut>.Fail(ErrorAr!);
}

/// <summary>نتيجة جلب بايتات نسخة وسائط — للمصغرات والمعاينة (ETag خادمي للتخزين المؤقت).</summary>
public sealed record MediaVariantFetch(bool Ok, byte[]? Bytes, string? MimeType, string? ErrorAr)
{
    public static MediaVariantFetch Ok(byte[] bytes, string mimeType) => new(true, bytes, mimeType, null);
    public static MediaVariantFetch Fail(string errorAr) => new(false, null, null, errorAr);
}

/// <summary>جسد استجابة فك الربط: معرف الربط وعدد الروابط المتبقية للأصل.</summary>
public sealed record UnlinkResponse(long LinkId, int RemainingLinksForAsset);

/// <summary>علامة «بلا جسد يُقرأ» لعمليات الحذف الناجحة (204).</summary>
public sealed record IgnoreBody;
