// Services/WeraApiClient.cs — WERA PRO HD Desktop (G3)
// العميل الوحيد للتعامل مع API: IHttpClientFactory + معرف ترابط لكل طلب +
// إرفاق رمز الوصول آلياً — كل النداءات async (لا تجميد UI §11.1).
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeraPro.Contracts.V1;

namespace WeraPro.Desktop.Services;

/// <summary>
/// معالج يضيف معرف الترابط وترويسة المصادقة لكل طلب صادر —
/// نفس X-Correlation-ID الذي يقرؤه الخادم ويوصله حتى audit_logs (§11.1/§20).
/// </summary>
public sealed class CorrelationHandler(SessionService session) : DelegatingHandler
{
    private static readonly string HeaderName = "X-Correlation-ID";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlationId = "wera-" + Guid.NewGuid().ToString("N")[..20];
        request.Headers.Remove(HeaderName);
        request.Headers.Add(HeaderName, correlationId);

        if (session.AccessToken is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);
        // 401 عند انتهاء صلاحية الوصول: محاولة تجديد شفافة واحدة ثم إعادة الطلب
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
            session.CanRefresh &&
            request.Headers.Authorization is not null &&
            !request.RequestUri!.AbsolutePath.EndsWith("/auth/refresh", StringComparison.OrdinalIgnoreCase) &&
            !request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            var refreshed = await session.TryRefreshAsync(cancellationToken);
            if (refreshed)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                response.Dispose();
                response = await base.SendAsync(request, cancellationToken);
            }
        }
        return response;
    }
}

/// <summary>
/// عميل API المكتوب (typed): يبني على HttpClient باسم مسجل في IHttpClientFactory.
/// كل الاستجابات تُفحص وتُترجم أخطاؤها إلى رسائل عربية واضحة — لا استثناء خام للواجهة.
/// </summary>
// ملاحظة: معامل SessionService حُذف (CS9113 — غير مقروء): المصادقة والتجديد
// يجريان داخل CorrelationHandler الذي يملك جلسته الخاصة.
public sealed class WeraApiClient(
    IHttpClientFactory factory,
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
            return MediaVariantFetch.Success(bytes, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
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
                return ApiCallResult<T>.Success(default!);
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

    // ── المخزون (G6 — §12.1/§12.2) ───────────────────────────────────────

    public async Task<ApiCallResult<List<WarehouseDto>>> GetWarehousesAsync(bool includeInactive, CancellationToken ct = default)
    {
        var result = await GetAsync<List<WarehouseDto>>(ApiRoutes.InventoryWarehouses + "?includeInactive=" + (includeInactive ? "true" : "false"), ct);
        return result.Map(v => v ?? []);
    }

    public async Task<ApiCallResult<WarehouseDto>> CreateWarehouseAsync(CreateWarehouseRequest request, CancellationToken ct = default)
        => await PostAsync<CreateWarehouseRequest, WarehouseDto>(ApiRoutes.InventoryWarehouses, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<IgnoreBody>> SetWarehouseStatusAsync(long warehouseId, bool isActive, string? reasonAr, CancellationToken ct = default)
        => await PostAsync<SetWarehouseStatusRequest, IgnoreBody>(
            InventoryWarehouseById(warehouseId) + "/status", new SetWarehouseStatusRequest(isActive, reasonAr), allowAnonymous: false, ct);

    public async Task<ApiCallResult<StockBalanceSearchResponse>> SearchBalancesAsync(
        string? query, long? warehouseId, bool? belowReorder, int page, int pageSize, CancellationToken ct = default)
    {
        var qs = new List<string> { "page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture), "pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(query)) qs.Add("query=" + Uri.EscapeDataString(query.Trim()));
        if (warehouseId is { } w) qs.Add("warehouseId=" + w.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (belowReorder is { } b) qs.Add("belowReorder=" + (b ? "true" : "false"));
        return await GetAsync<StockBalanceSearchResponse>(ApiRoutes.InventoryBalances + "?" + string.Join("&", qs), ct);
    }

    public async Task<ApiCallResult<InventoryValuationResponse>> GetValuationAsync(long? warehouseId, CancellationToken ct = default)
        => await GetAsync<InventoryValuationResponse>(ApiRoutes.InventoryValuation + (warehouseId is { } w ? "?warehouseId=" + w.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""), ct);

    public async Task<ApiCallResult<List<StockMovementCardDto>>> GetMovementCardAsync(long skuId, long? warehouseId, CancellationToken ct = default)
        => await GetAsync<List<StockMovementCardDto>>(
            ApiRoutes.InventoryBalances.Replace("balances", "skus", StringComparison.Ordinal) + $"/{skuId}/movements" + (warehouseId is { } w ? "?warehouseId=" + w.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""), ct);

    public async Task<ApiCallResult<List<ReservationDto>>> GetReservationsAsync(bool activeOnly, CancellationToken ct = default)
    {
        var result = await GetAsync<List<ReservationDto>>(ApiRoutes.InventoryReservations + "?activeOnly=" + (activeOnly ? "true" : "false"), ct);
        return result.Map(v => v ?? []);
    }

    public async Task<ApiCallResult<ReservationDto>> ReserveStockAsync(ReserveStockRequest request, CancellationToken ct = default)
        => await PostAsync<ReserveStockRequest, ReservationDto>(ApiRoutes.InventoryReservations, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<IgnoreBody>> ReleaseReservationAsync(long reservationId, string? reasonAr, CancellationToken ct = default)
        => await PostAsync<ReleaseReservationRequest, IgnoreBody>(
            ApiRoutes.InventoryReservations + $"/{reservationId}/release", new ReleaseReservationRequest(reasonAr), allowAnonymous: false, ct);

    public async Task<ApiCallResult<StocktakeDto>> OpenStocktakeAsync(OpenStocktakeRequest request, CancellationToken ct = default)
        => await PostAsync<OpenStocktakeRequest, StocktakeDto>(ApiRoutes.InventoryStocktakes, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<StocktakeDetailsResponse>> RecordCountLinesAsync(long countId, RecordCountLinesRequest request, CancellationToken ct = default)
        => await PostAsync<RecordCountLinesRequest, StocktakeDetailsResponse>(
            InventoryStocktakeById(countId) + "/lines", request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<IgnoreBody>> StocktakeActionAsync(long countId, string action, CancellationToken ct = default)
        => await PostAsync<object, IgnoreBody>(InventoryStocktakeById(countId) + "/" + action, new { }, allowAnonymous: false, ct);

    public async Task<ApiCallResult<ApproveStocktakeResponse>> ApproveStocktakeAsync(long countId, CancellationToken ct = default)
        => await PostAsync<object, ApproveStocktakeResponse>(InventoryStocktakeById(countId) + "/approve", new { }, allowAnonymous: false, ct);

    public async Task<ApiCallResult<List<StocktakeDto>>> GetStocktakesAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var result = await GetAsync<StocktakeListResponse>(ApiRoutes.InventoryStocktakes + "?page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        return result.Map(v => v?.Items is { } items ? items.ToList() : []);
    }

    public async Task<ApiCallResult<StocktakeDetailsResponse>> GetStocktakeAsync(long countId, CancellationToken ct = default)
        => await GetAsync<StocktakeDetailsResponse>(InventoryStocktakeById(countId), ct);

    public async Task<ApiCallResult<PostAdjustmentResponse>> PostAdjustmentAsync(PostAdjustmentRequest request, CancellationToken ct = default)
        => await PostAsync<PostAdjustmentRequest, PostAdjustmentResponse>(ApiRoutes.InventoryAdjustments, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<TransferDto>> CreateTransferAsync(CreateTransferRequest request, CancellationToken ct = default)
        => await PostAsync<CreateTransferRequest, TransferDto>(ApiRoutes.InventoryTransfers, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<TransferActionResponse>> SendTransferAsync(long transferId, long? approvalId, CancellationToken ct = default)
        => await PostAsync<SendTransferRequest, TransferActionResponse>(
            InventoryTransferById(transferId) + "/send", new SendTransferRequest(approvalId, null), allowAnonymous: false, ct);

    public async Task<ApiCallResult<TransferActionResponse>> ReceiveTransferAsync(long transferId, CancellationToken ct = default)
        => await PostAsync<object, TransferActionResponse>(InventoryTransferById(transferId) + "/receive", new { }, allowAnonymous: false, ct);

    public async Task<ApiCallResult<IgnoreBody>> CancelTransferAsync(long transferId, CancellationToken ct = default)
        => await PostAsync<object, IgnoreBody>(InventoryTransferById(transferId) + "/cancel", new { }, allowAnonymous: false, ct);

    public async Task<ApiCallResult<List<TransferDto>>> GetTransfersAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var qs = new List<string> { "page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture), "pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(status)) qs.Add("status=" + Uri.EscapeDataString(status));
        var result = await GetAsync<List<TransferDto>>(ApiRoutes.InventoryTransfers + "?" + string.Join("&", qs), ct);
        return result.Map(v => v ?? []);
    }

    public async Task<ApiCallResult<TransferDto>> GetTransferAsync(long transferId, CancellationToken ct = default)
        => await GetAsync<TransferDto>(InventoryTransferById(transferId), ct);

    public async Task<ApiCallResult<List<ReorderSuggestionDto>>> GetReorderAsync(long? warehouseId, CancellationToken ct = default)
    {
        var result = await GetAsync<ReorderReportResponse>(ApiRoutes.InventoryReorder + (warehouseId is { } w ? "?warehouseId=" + w.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""), ct);
        return result.Map(v => v?.Items is { } items ? items.ToList() : []);
    }

    public async Task<ApiCallResult<List<LotDto>>> GetLotsAsync(int? expiringInDays, CancellationToken ct = default)
    {
        var result = await GetAsync<LotsReportResponse>(ApiRoutes.InventoryLots + (expiringInDays is { } d ? "?expiringInDays=" + d.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""), ct);
        return result.Map(v => v?.Items is { } items ? items.ToList() : []);
    }

    // ── الاعتمادات (G6 — D-03 ب) ─────────────────────────────────────────

    public async Task<ApiCallResult<ApprovalDto>> CreateApprovalAsync(CreateApprovalRequest request, CancellationToken ct = default)
        => await PostAsync<CreateApprovalRequest, ApprovalDto>(ApiRoutes.Approvals, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<ApprovalDto>> DecideApprovalAsync(long approvalId, DecideApprovalRequest request, CancellationToken ct = default)
        => await PostAsync<DecideApprovalRequest, ApprovalDto>(ApiRoutes.Approvals + $"/{approvalId}/decision", request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<List<ApprovalDto>>> GetApprovalsAsync(string? status, CancellationToken ct = default)
    {
        var qs = string.IsNullOrWhiteSpace(status) ? "" : "?status=" + Uri.EscapeDataString(status);
        var result = await GetAsync<ApprovalListResponse>(ApiRoutes.Approvals + qs, ct);
        return result.Map(v => v?.Items is { } items ? items.ToList() : []);
    }

    // ── المشتريات (G6 — §12.3) ───────────────────────────────────────────

    public async Task<ApiCallResult<SupplierSearchResponse>> SearchSuppliersAsync(string? query, int page, int pageSize, CancellationToken ct = default)
    {
        var qs = new List<string> { "page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture), "pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(query)) qs.Add("query=" + Uri.EscapeDataString(query.Trim()));
        return await GetAsync<SupplierSearchResponse>(ApiRoutes.PurchasingSuppliers + "?" + string.Join("&", qs), ct);
    }

    public async Task<ApiCallResult<SupplierDto>> CreateSupplierAsync(CreateSupplierRequest request, CancellationToken ct = default)
        => await PostAsync<CreateSupplierRequest, SupplierDto>(ApiRoutes.PurchasingSuppliers, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<IgnoreBody>> SetSupplierStatusAsync(long supplierId, string status, string? reasonAr, CancellationToken ct = default)
        => await PostAsync<SetPartyStatusRequest, IgnoreBody>(
            PurchasingSupplierById(supplierId) + "/status", new SetPartyStatusRequest(status, reasonAr), allowAnonymous: false, ct);

    public async Task<ApiCallResult<PartyStatementResponse>> GetSupplierStatementAsync(long supplierId, DateOnly? from, DateOnly? to, string? docType, CancellationToken ct = default)
        => await GetAsync<PartyStatementResponse>(PartyStatementUrl(PurchasingSupplierById(supplierId) + "/statement", from, to, docType), ct);

    public async Task<ApiCallResult<PurchaseOrderDto>> CreatePurchaseOrderAsync(CreatePurchaseOrderRequest request, CancellationToken ct = default)
        => await PostAsync<CreatePurchaseOrderRequest, PurchaseOrderDto>(ApiRoutes.PurchasingOrders, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<PurchaseOrderDto>> GetPurchaseOrderAsync(long poId, CancellationToken ct = default)
        => await GetAsync<PurchaseOrderDto>(PurchasingOrderById(poId), ct);

    public async Task<ApiCallResult<List<PurchaseOrderDto>>> GetPurchaseOrdersAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var qs = new List<string> { "page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture), "pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(status)) qs.Add("status=" + Uri.EscapeDataString(status));
        var result = await GetAsync<List<PurchaseOrderDto>>(ApiRoutes.PurchasingOrders + "?" + string.Join("&", qs), ct);
        return result.Map(v => v ?? []);
    }

    public async Task<ApiCallResult<PurchaseOrderDto>> ReplacePoLinesAsync(long poId, ReplacePurchaseOrderLinesRequest request, CancellationToken ct = default)
        => await PutAsync<ReplacePurchaseOrderLinesRequest, PurchaseOrderDto>(PurchasingOrderById(poId) + "/lines", request, ct);

    // ملاحظة: نقطتا submit/cancel لأوامر الشراء لا تقبلان جسداً (المسار فقط) —
    // سبب التغيير: ClosePoRequest لم يكن معرّفاً في العقود أصلاً.
    public async Task<ApiCallResult<PoStatusChangeResponse>> PoActionAsync(long poId, string action, string? reasonAr, CancellationToken ct = default)
        => await PostAsync<object, PoStatusChangeResponse>(
            PurchasingOrderById(poId) + "/" + action, new { }, allowAnonymous: false, ct);

    public async Task<ApiCallResult<ReceiptDto>> CreateReceiptAsync(CreateReceiptRequest request, CancellationToken ct = default)
        => await PostAsync<CreateReceiptRequest, ReceiptDto>(ApiRoutes.PurchasingReceipts, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<ReceiptDto>> GetReceiptAsync(long receiptId, CancellationToken ct = default)
        => await GetAsync<ReceiptDto>(PurchasingReceiptById(receiptId), ct);

    public async Task<ApiCallResult<List<ReceiptDto>>> GetReceiptsAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var qs = new List<string> { "page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture), "pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(status)) qs.Add("status=" + Uri.EscapeDataString(status));
        var result = await GetAsync<List<ReceiptDto>>(ApiRoutes.PurchasingReceipts + "?" + string.Join("&", qs), ct);
        return result.Map(v => v ?? []);
    }

    public async Task<ApiCallResult<PostReceiptResponse>> PostReceiptAsync(long receiptId, string? idempotencyKey, CancellationToken ct = default)
        => await PostAsync<PostDocumentRequest, PostReceiptResponse>(
            PurchasingReceiptById(receiptId) + "/post", new PostDocumentRequest(idempotencyKey), allowAnonymous: false, ct);

    public async Task<ApiCallResult<ReceiptDto>> CancelReceiptAsync(long receiptId, CancellationToken ct = default)
        => await PostAsync<object, ReceiptDto>(PurchasingReceiptById(receiptId) + "/cancel", new { }, allowAnonymous: false, ct);

    public async Task<ApiCallResult<SupplierInvoiceDto>> CreateInvoiceAsync(CreateSupplierInvoiceRequest request, CancellationToken ct = default)
        => await PostAsync<CreateSupplierInvoiceRequest, SupplierInvoiceDto>(ApiRoutes.PurchasingInvoices, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<List<SupplierInvoiceDto>>> GetInvoicesAsync(string? status, long? supplierId, int page, int pageSize, CancellationToken ct = default)
    {
        var qs = new List<string> { "page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture), "pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(status)) qs.Add("status=" + Uri.EscapeDataString(status));
        if (supplierId is { } sup) qs.Add("supplierId=" + sup.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = await GetAsync<List<SupplierInvoiceDto>>(ApiRoutes.PurchasingInvoices + "?" + string.Join("&", qs), ct);
        return result.Map(v => v ?? []);
    }

    public async Task<ApiCallResult<SupplierInvoiceDto>> GetInvoiceAsync(long invoiceId, CancellationToken ct = default)
        => await GetAsync<SupplierInvoiceDto>(PurchasingInvoiceById(invoiceId), ct);

    public async Task<ApiCallResult<PostInvoiceResponse>> PostInvoiceAsync(long invoiceId, string? idempotencyKey, CancellationToken ct = default)
        => await PostAsync<PostDocumentRequest, PostInvoiceResponse>(
            PurchasingInvoiceById(invoiceId) + "/post", new PostDocumentRequest(idempotencyKey), allowAnonymous: false, ct);

    public async Task<ApiCallResult<SupplierInvoiceDto>> CancelInvoiceAsync(long invoiceId, CancellationToken ct = default)
        => await PostAsync<object, SupplierInvoiceDto>(PurchasingInvoiceById(invoiceId) + "/cancel", new { }, allowAnonymous: false, ct);

    public async Task<ApiCallResult<SupplierPaymentDto>> CreatePaymentAsync(CreateSupplierPaymentRequest request, CancellationToken ct = default)
        => await PostAsync<CreateSupplierPaymentRequest, SupplierPaymentDto>(ApiRoutes.PurchasingPayments, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<List<SupplierPaymentDto>>> GetPaymentsAsync(long? supplierId, string? status, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (supplierId is { } sup) qs.Add("supplierId=" + sup.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(status)) qs.Add("status=" + Uri.EscapeDataString(status));
        var url = ApiRoutes.PurchasingPayments + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        var result = await GetAsync<List<SupplierPaymentDto>>(url, ct);
        return result.Map(v => v ?? []);
    }

    public async Task<ApiCallResult<PostPaymentResponse>> PostPaymentAsync(long paymentId, string? idempotencyKey, CancellationToken ct = default)
        => await PostAsync<PostDocumentRequest, PostPaymentResponse>(
            ApiRoutes.PurchasingPayments + $"/{paymentId}/post", new PostDocumentRequest(idempotencyKey), allowAnonymous: false, ct);

    public async Task<ApiCallResult<SupplierPaymentDto>> CancelPaymentAsync(long paymentId, CancellationToken ct = default)
        => await PostAsync<object, SupplierPaymentDto>(ApiRoutes.PurchasingPayments + $"/{paymentId}/cancel", new { }, allowAnonymous: false, ct);

    public async Task<ApiCallResult<SupplierReturnDto>> CreateReturnAsync(CreateSupplierReturnRequest request, CancellationToken ct = default)
        => await PostAsync<CreateSupplierReturnRequest, SupplierReturnDto>(ApiRoutes.PurchasingReturns, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<List<SupplierReturnDto>>> GetReturnsAsync(string? status, long? supplierId, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(status)) qs.Add("status=" + Uri.EscapeDataString(status));
        if (supplierId is { } sup) qs.Add("supplierId=" + sup.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var url = ApiRoutes.PurchasingReturns + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        var result = await GetAsync<List<SupplierReturnDto>>(url, ct);
        return result.Map(v => v ?? []);
    }

    public async Task<ApiCallResult<PostReturnResponse>> PostReturnAsync(long returnId, string? idempotencyKey, CancellationToken ct = default)
        => await PostAsync<PostDocumentRequest, PostReturnResponse>(
            ApiRoutes.PurchasingReturns + $"/{returnId}/post", new PostDocumentRequest(idempotencyKey), allowAnonymous: false, ct);

    public async Task<ApiCallResult<SupplierReturnDto>> CancelReturnAsync(long returnId, CancellationToken ct = default)
        => await PostAsync<object, SupplierReturnDto>(ApiRoutes.PurchasingReturns + $"/{returnId}/cancel", new { }, allowAnonymous: false, ct);

    public async Task<ApiCallResult<List<TreasuryAccountDto>>> GetTreasuryAccountsAsync(CancellationToken ct = default)
    {
        var result = await GetAsync<TreasuryAccountListResponse>(ApiRoutes.PurchasingTreasuryAccounts, ct);
        return result.Map(v => v?.Items is { } items ? items.ToList() : []);
    }

    // ── الأطراف (G6 — §13) ───────────────────────────────────────────────

    public async Task<ApiCallResult<CustomerSearchResponse>> SearchCustomersAsync(string? query, int page, int pageSize, CancellationToken ct = default)
    {
        var qs = new List<string> { "page=" + page.ToString(System.Globalization.CultureInfo.InvariantCulture), "pageSize=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(query)) qs.Add("query=" + Uri.EscapeDataString(query.Trim()));
        return await GetAsync<CustomerSearchResponse>(ApiRoutes.PartiesCustomers + "?" + string.Join("&", qs), ct);
    }

    public async Task<ApiCallResult<CustomerDto>> CreateCustomerAsync(CreateCustomerRequest request, CancellationToken ct = default)
        => await PostAsync<CreateCustomerRequest, CustomerDto>(ApiRoutes.PartiesCustomers, request, allowAnonymous: false, ct);

    public async Task<ApiCallResult<IgnoreBody>> SetCustomerStatusAsync(long customerId, string status, string? reasonAr, CancellationToken ct = default)
        => await PostAsync<SetPartyStatusRequest, IgnoreBody>(
            ApiRoutes.PartiesCustomers + $"/{customerId}/status", new SetPartyStatusRequest(status, reasonAr), allowAnonymous: false, ct);

    public async Task<ApiCallResult<PartyStatementResponse>> GetCustomerStatementAsync(long customerId, DateOnly? from, DateOnly? to, string? docType, CancellationToken ct = default)
        => await GetAsync<PartyStatementResponse>(PartyStatementUrl(ApiRoutes.PartiesCustomers + $"/{customerId}/statement", from, to, docType), ct);

    private static string InventoryWarehouseById(long id) => ApiRoutes.InventoryWarehouses + $"/{id}";
    private static string InventoryStocktakeById(long id) => ApiRoutes.InventoryStocktakes + $"/{id}";
    private static string InventoryTransferById(long id) => ApiRoutes.InventoryTransfers + $"/{id}";
    private static string PurchasingSupplierById(long id) => ApiRoutes.PurchasingSuppliers + $"/{id}";
    private static string PurchasingOrderById(long id) => ApiRoutes.PurchasingOrders + $"/{id}";
    private static string PurchasingReceiptById(long id) => ApiRoutes.PurchasingReceipts + $"/{id}";
    private static string PurchasingInvoiceById(long id) => ApiRoutes.PurchasingInvoices + $"/{id}";

    private static string PartyStatementUrl(string baseUri, DateOnly? from, DateOnly? to, string? docType)
    {
        var qs = new List<string>();
        if (from is { } f) qs.Add("from=" + f.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        if (to is { } t) qs.Add("to=" + t.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(docType)) qs.Add("docType=" + Uri.EscapeDataString(docType));
        return baseUri + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
    }

    private async Task<ApiCallResult<TRes>> PutAsync<TReq, TRes>(string url, TReq body, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.PutAsJsonAsync(url, body, ct);
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


    // ── POS (G7 — §9) ────────────────────────────────────────────────────

    // ═══════════════ G8: الخزائن والسندات والمصروفات ورأس المال والورديات (§14/§13) ═══════════════

    // ═══════════════ G8: الخزائن والسندات والمصروفات ورأس المال والورديات (§14/§13) ═══════════════

    public Task<ApiCallResult<List<TreasuryAccountBalanceDto>>> GetTreasuryAccountsAsync(bool includeInactive = false, CancellationToken ct = default)
        => GetAsync<List<TreasuryAccountBalanceDto>>(Fin("/treasury/accounts") + (includeInactive ? "?includeInactive=true" : ""), ct);

    public Task<ApiCallResult<TreasuryTxnPagedDto>> GetTreasuryTransactionsAsync(long treasuryAccountId, int page = 1, int pageSize = 50, CancellationToken ct = default)
        => GetAsync<TreasuryTxnPagedDto>(Fin($"/treasury/transactions?treasuryAccountId={treasuryAccountId}&page={page}&pageSize={pageSize}"), ct);

    public Task<ApiCallResult<TreasuryAccountBalanceDto>> CreateTreasuryAccountAsync(CreateTreasuryAccountRequest request, CancellationToken ct = default)
        => PostAsync<CreateTreasuryAccountRequest, TreasuryAccountBalanceDto>(Fin("/treasury/accounts"), request, allowAnonymous: false, ct);

    public Task<ApiCallResult<VoucherDto>> CreateVoucherAsync(CreateVoucherRequest request, CancellationToken ct = default)
        => PostAsync<CreateVoucherRequest, VoucherDto>(Fin("/vouchers"), request, allowAnonymous: false, ct);

    public Task<ApiCallResult<VoucherPagedDto>> GetVouchersAsync(string? type = null, string? status = null, int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        var qs = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(type)) qs.Add("type=" + Uri.EscapeDataString(type));
        if (!string.IsNullOrWhiteSpace(status)) qs.Add("status=" + Uri.EscapeDataString(status));
        return GetAsync<VoucherPagedDto>(Fin("/vouchers?") + string.Join("&", qs), ct);
    }

    public Task<ApiCallResult<VoucherDto>> PostVoucherAsync(long voucherId, CancellationToken ct = default)
        => PostAsync<object, VoucherDto>(Fin($"/vouchers/{voucherId}/post"), new { }, allowAnonymous: false, ct);

    public Task<ApiCallResult<IgnoreBody>> CancelVoucherAsync(long voucherId, string reasonAr, CancellationToken ct = default)
        => PostAsync<CancelRequest, IgnoreBody>(Fin($"/vouchers/{voucherId}/cancel"), new CancelRequest(reasonAr), allowAnonymous: false, ct);

    public Task<ApiCallResult<List<CustomerReceivableDto>>> GetCustomerReceivablesAsync(long customerId, CancellationToken ct = default)
        => GetAsync<List<CustomerReceivableDto>>(Fin($"/customers/{customerId}/receivables"), ct);

    public Task<ApiCallResult<ExpenseDto>> CreateExpenseAsync(CreateExpenseRequest request, CancellationToken ct = default)
        => PostAsync<CreateExpenseRequest, ExpenseDto>(Fin("/expenses"), request, allowAnonymous: false, ct);

    public Task<ApiCallResult<ExpensePagedDto>> GetExpensesAsync(string? status = null, long? shiftId = null, int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        var qs = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(status)) qs.Add("status=" + Uri.EscapeDataString(status));
        if (shiftId is { } sid) qs.Add($"shiftId={sid}");
        return GetAsync<ExpensePagedDto>(Fin("/expenses?") + string.Join("&", qs), ct);
    }

    public Task<ApiCallResult<ExpenseDto>> ApproveExpenseAsync(long expenseId, string reasonAr, CancellationToken ct = default)
        => PostAsync<CancelRequest, ExpenseDto>(Fin($"/expenses/{expenseId}/approve"), new CancelRequest(reasonAr), allowAnonymous: false, ct);

    public Task<ApiCallResult<ExpensePostResponse>> PostExpenseAsync(long expenseId, CancellationToken ct = default)
        => PostAsync<object, ExpensePostResponse>(Fin($"/expenses/{expenseId}/post"), new { }, allowAnonymous: false, ct);

    public Task<ApiCallResult<ExpenseDto>> CancelExpenseAsync(long expenseId, string reasonAr, CancellationToken ct = default)
        => PostAsync<CancelRequest, ExpenseDto>(Fin($"/expenses/{expenseId}/cancel"), new CancelRequest(reasonAr), allowAnonymous: false, ct);

    public Task<ApiCallResult<List<ExpenseCategoryDto>>> GetExpenseCategoriesAsync(CancellationToken ct = default)
        => GetAsync<List<ExpenseCategoryDto>>(Fin("/expense-categories"), ct);

    public Task<ApiCallResult<CapitalEventDto>> CreateCapitalEventAsync(CreateCapitalEventRequest request, CancellationToken ct = default)
        => PostAsync<CreateCapitalEventRequest, CapitalEventDto>(Fin("/capital/events"), request, allowAnonymous: false, ct);

    public Task<ApiCallResult<CapitalPagedDto>> GetCapitalEventsAsync(string? eventType = null, string? status = null, int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        var qs = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(eventType)) qs.Add("eventType=" + Uri.EscapeDataString(eventType));
        if (!string.IsNullOrWhiteSpace(status)) qs.Add("status=" + Uri.EscapeDataString(status));
        return GetAsync<CapitalPagedDto>(Fin("/capital/events?") + string.Join("&", qs), ct);
    }

    public Task<ApiCallResult<CapitalEventDto>> PostCapitalEventAsync(long capitalId, CancellationToken ct = default)
        => PostAsync<object, CapitalEventDto>(Fin($"/capital/events/{capitalId}/post"), new { }, allowAnonymous: false, ct);

    public Task<ApiCallResult<ShiftPagedDto>> GetShiftsAsync(long? stationId = null, string? status = null, int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        var qs = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (stationId is { } sid) qs.Add($"stationId={sid}");
        if (!string.IsNullOrWhiteSpace(status)) qs.Add("status=" + Uri.EscapeDataString(status));
        return GetAsync<ShiftPagedDto>("/" + Contracts.V1.SalesApi.Prefix + "/shifts?" + string.Join("&", qs), ct);
    }

    public Task<ApiCallResult<ShiftReconciliationDto>> GetShiftReconciliationAsync(long shiftId, CancellationToken ct = default)
        => GetAsync<ShiftReconciliationDto>("/" + Contracts.V1.SalesApi.Prefix + $"/shifts/{shiftId}/reconciliation", ct);

    public Task<ApiCallResult<ShiftCloseResponse>> CloseShiftAsync(long shiftId, CloseShiftRequest request, CancellationToken ct = default)
        => PostAsync<CloseShiftRequest, ShiftCloseResponse>("/" + Contracts.V1.SalesApi.Prefix + $"/shifts/{shiftId}/close", request, allowAnonymous: false, ct);

    public Task<ApiCallResult<ShiftCashMovementsDto>> GetShiftCashMovementsAsync(long shiftId, CancellationToken ct = default)
        => GetAsync<ShiftCashMovementsDto>("/" + Contracts.V1.SalesApi.Prefix + $"/shifts/{shiftId}/cash-movements", ct);

    public Task<ApiCallResult<FinancePrintResponse>> PrintFinanceDocumentAsync(PrintFinanceDocumentRequest request, CancellationToken ct = default)
        => PostAsync<PrintFinanceDocumentRequest, FinancePrintResponse>(Fin("/print"), request, allowAnonymous: false, ct);

    private static string Pos(string path) => Contracts.V1.SalesApi.Prefix + path;
    private static string Fin(string path) => Contracts.V1.FinanceApi.Prefix + path;

    /// <summary>سياق المحطة (§9.1A): الشريط الأعلى يعرضه عند فتح POS.</summary>
    public Task<ApiCallResult<PosContextResponse>> GetPosContextAsync(CancellationToken ct = default)
        => GetAsync<PosContextResponse>(Pos("/context"), ct);

    /// <summary>الالتقاط: باركود/SKU/PLU + باركود الوزن المتغير (Exact Match §3.1/§5.3).</summary>
    public Task<ApiCallResult<PosLookupResponse>> PosLookupAsync(string code, CancellationToken ct = default)
        => PostAsync<object, PosLookupResponse>(Pos("/lookup"), new { code }, allowAnonymous: false, ct);

    /// <summary>البحث النصي المحدود المرقم خادمياً (§9.2).</summary>
    public Task<ApiCallResult<PosSearchResponse>> PosSearchAsync(string query, int page = 1, int pageSize = 20, CancellationToken ct = default)
        => GetAsync<PosSearchResponse>(Pos($"/search?query={Uri.EscapeDataString(query)}&page={page}&pageSize={pageSize}"), ct);

    /// <summary>معاينة التسعير الخادمية (§9.0 ق10 — المجاميع من الخادم فقط، لا حساب في العميل).</summary>
    public Task<ApiCallResult<PosPreviewResponse>> PosPreviewAsync(PosPreviewRequest request, CancellationToken ct = default)
        => PostAsync<PosPreviewRequest, PosPreviewResponse>(Pos("/preview"), request, allowAnonymous: false, ct);

    /// <summary>معاينة الوزن خادمياً — الطرق الست (§9.3) تُحسب في خدمة المجال لا في العميل.</summary>
    public Task<ApiCallResult<WeightPreviewResponse>> PosWeightPreviewAsync(WeightPreviewRequest request, CancellationToken ct = default)
        => PostAsync<WeightPreviewRequest, WeightPreviewResponse>(Pos("/weight/preview"), request, allowAnonymous: false, ct);

    /// <summary>إتمام البيع (§9.7) — مفتاح Idempotency يمنع الازدواج عند timeout (§15.4).</summary>
    public Task<ApiCallResult<PostedSaleResponse>> PostSaleAsync(PostSaleRequest request, CancellationToken ct = default)
        => PostAsync<PostSaleRequest, PostedSaleResponse>(Pos("/sales"), request, allowAnonymous: false, ct);

    public Task<ApiCallResult<PosSaleDetailResponse>> GetSaleAsync(long invoiceId, CancellationToken ct = default)
        => GetAsync<PosSaleDetailResponse>(Pos("/sales/" + invoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct);

    public Task<ApiCallResult<PosSaleListResponse>> ListRecentSalesAsync(int limit = 10, long? shiftId = null, CancellationToken ct = default)
        => GetAsync<PosSaleListResponse>(Pos($"/sales?limit={limit}" + (shiftId is { } sid ? $"&shiftId={sid}" : string.Empty)), ct);

    public Task<ApiCallResult<JsonElement>> VoidSaleAsync(long invoiceId, string reasonAr, CancellationToken ct = default)
        => PostAsync<object, JsonElement>(Pos("/sales/" + invoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/void"),
            new { reasonAr }, allowAnonymous: false, ct);

    /// <summary>تعليق سلة (§9.5) — لقطة JSON للسلة الحالية؛ الاستعادة قراءة لا تنشئ شيئاً.</summary>
    public Task<ApiCallResult<JsonElement>> HoldCartAsync(string cartJson, string? noteAr, long? customerId, CancellationToken ct = default)
        => PostAsync<object, JsonElement>(Pos("/carts/hold"), new { cartJson, noteAr, customerId }, allowAnonymous: false, ct);

    public Task<ApiCallResult<HeldCartDto[]>> ListHeldCartsAsync(CancellationToken ct = default)
        => GetAsync<HeldCartDto[]>(Pos("/carts/held"), ct);

    public Task<ApiCallResult<RestoreCartResponse>> RestoreCartAsync(long cartId, CancellationToken ct = default)
        => PostWithResponseAsync<RestoreCartResponse>(Pos("/carts/" + cartId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/restore"), ct);

    /// <summary>مرتجع/استبدال (§9.8 · D-04) — اعتماد مشرف إلزامي ورد بوسيلة الدفع الأصلية.</summary>
    public Task<ApiCallResult<PostedRefundResponse>> PostRefundAsync(PostRefundRequest request, CancellationToken ct = default)
        => PostAsync<PostRefundRequest, PostedRefundResponse>(Pos("/refunds"), request, allowAnonymous: false, ct);

    /// <summary>الوردية (§9.9): فتح وربط الفواتير وملخص المتوقع في الدرج.</summary>
    public Task<ApiCallResult<ShiftDto>> OpenShiftAsync(decimal openingFloat, CancellationToken ct = default)
        => PostAsync<object, ShiftDto>(Pos("/shifts"), new { openingFloat }, allowAnonymous: false, ct);

    public Task<ApiCallResult<ShiftDto>> GetCurrentShiftAsync(CancellationToken ct = default)
        => GetAsync<ShiftDto>(Pos("/shifts/current"), ct);

    public Task<ApiCallResult<ShiftSummaryDto>> GetShiftSummaryAsync(long shiftId, CancellationToken ct = default)
        => GetAsync<ShiftSummaryDto>(Pos("/shifts/" + shiftId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/summary"), ct);

    /// <summary>فتح الدرج بغير حركة بيع — سبب مسجل وUNKNOWN صادقة حتى تكامل الأجهزة (§9.9/B-01).</summary>
    public Task<ApiCallResult<DrawerEventResponse>> OpenDrawerAsync(string reasonAr, CancellationToken ct = default)
        => PostAsync<object, DrawerEventResponse>(Pos("/drawer/open"), new { reasonAr }, allowAnonymous: false, ct);

    /// <summary>مهمة إيصال (§29.4/§29.9): توجيه تلقائي بقالب المحطة أو HOLD موثق — لا فشل صامت.</summary>
    public Task<ApiCallResult<PrintJobDto>> PrintReceiptAsync(long invoiceId, string copyType, CancellationToken ct = default)
        => PostAsync<object, PrintJobDto>(
            Pos("/sales/" + invoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/print"),
            new { copyType }, allowAnonymous: false, ct);

    public Task<ApiCallResult<PrintJobDto[]>> ListPrintJobsAsync(long invoiceId, CancellationToken ct = default)
        => GetAsync<PrintJobDto[]>(Pos("/sales/" + invoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/print-jobs"), ct);

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
            return ApiCallResult<T>.Success(payload!);
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
    public static ApiCallResult<T> Success(T value) => new(true, value, null);
    public static ApiCallResult<T> Fail(string errorAr) => new(false, default, errorAr);

    public ApiCallResult<TOut> Map<TOut>(Func<T, TOut> map) =>
        Ok ? ApiCallResult<TOut>.Success(map(Value!)) : ApiCallResult<TOut>.Fail(ErrorAr!);
}

/// <summary>نتيجة جلب بايتات نسخة وسائط — للمصغرات والمعاينة (ETag خادمي للتخزين المؤقت).</summary>
public sealed record MediaVariantFetch(bool Ok, byte[]? Bytes, string? MimeType, string? ErrorAr)
{
    public static MediaVariantFetch Success(byte[] bytes, string mimeType) => new(true, bytes, mimeType, null);
    public static MediaVariantFetch Fail(string errorAr) => new(false, null, null, errorAr);
}

/// <summary>جسد استجابة فك الربط: معرف الربط وعدد الروابط المتبقية للأصل.</summary>
public sealed record UnlinkResponse(long LinkId, int RemainingLinksForAsset);

/// <summary>علامة «بلا جسد يُقرأ» لعمليات الحذف الناجحة (204).</summary>
public sealed record IgnoreBody;
