namespace WeraPro.Api.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using SkiaSharp;
using Xunit;

/// <summary>
/// فحوص بوابة G5 (§6 مكتبة الوسائط): رفع/تكرر/تالف/غير مدعوم/قنبلة ترويسة ·
/// ربط وفك ربط وصورة رئيسية · حذف أصل محمي بالمراجعة · أولوية SKU←منتج ·
/// مهام استيراد كاملة (إلغاء آمن/استئناف/تعافي انهيار/تقرير لكل ملف) ·
/// RBAC خادمي (قراءة للجميع وإدارة واستيراد لمن يملكه) — على PostgreSQL
/// حقيقي مُرحّل حتى رأس V016 مع عامل خلفي حي في نفس المضيف.
/// </summary>
public sealed class MediaEndpointTests : IClassFixture<MediaApiFixture>
{
    private readonly MediaApiFixture _fx;

    public MediaEndpointTests(MediaApiFixture fx) => _fx = fx;

    private async Task<HttpClient> ClientAsync(string username, string password)
    {
        var login = await _fx.LoginAsync(username, password);
        var client = _fx.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }

    private static MultipartFormDataContent MultipartOf((string Name, byte[] Bytes) file)
    {
        var content = new MultipartFormDataContent();
        var stream = new MemoryStream(file.Bytes);
        var part = new StreamContent(stream);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, "file", file.Name);
        return content;
    }

    // ══ الرفع والتحقق ═════════════════════════════════════════════════════

    [Fact]
    public async Task Upload_ValidPng_201_AllVariantsReachable_BytesOnDisk()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var png = MediaImageFactory.GradientPng(64, 48);

        using var form = MultipartOf(("جبنة رومي.png", png));
        var response = await client.PostAsync("/api/v1/media", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var mediaId = body.GetProperty("media").GetProperty("mediaId").GetInt64();
        Assert.Equal("image/png", body.GetProperty("media").GetProperty("mimeType").GetString());
        Assert.Equal(64, body.GetProperty("media").GetProperty("widthPx").GetInt32());
        Assert.True(body.GetProperty("media").GetProperty("hasThumbPos").GetBoolean());

        // النسخ الأربع تُجلب بمحتوى صحيح (الكتالوج/POS/المعاينة/الأصل)
        foreach (var variant in new[] { "original", "thumb-catalog", "thumb-pos", "preview" })
        {
            using var content = await client.GetAsync($"/api/v1/media/{mediaId}/content/{variant}");
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Equal("image/png", content.Content.Headers.ContentType?.MediaType);
            var bytes = await content.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 0, variant + " فارغة");
            // كل نسخة صورة سليمة قابلة للفك
            Assert.NotNull(SKBitmap.Decode(bytes));
        }

        // ahash محفوظ + الملفات موجودة فعلاً على القرص تحت الجذر المدار
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT ahash, thumb_catalog_key FROM catalog.media_assets WHERE media_id=@m", conn);
        cmd.Parameters.AddWithValue("m", mediaId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.IsDBNull(0), "ahash مفقود");
        Assert.False(reader.IsDBNull(1), "مفتاح نسخة الكتالوج مفقود");
    }

    [Fact]
    public async Task Upload_DuplicateBytes_409_ReturnsExisting_NoSecondRow()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var png = MediaImageFactory.GradientPng(40, 30, seed: 77);

        using (var form = MultipartOf(("dup.png", png)))
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/v1/media", form)).StatusCode);
        }
        using (var form2 = MultipartOf(("dup-again.png", png)))
        {
            var second = await client.PostAsync("/api/v1/media", form2);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            var body = await second.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("created").GetBoolean());
            Assert.True(body.GetProperty("existingMediaId").GetInt64() > 0);
        }

        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant();
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var count = new NpgsqlCommand(
            "SELECT COUNT(*) FROM catalog.media_assets WHERE sha256 = @s", conn);
        count.Parameters.AddWithValue("s", sha);
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Upload_TruncatedPng_400_Corrupt_NoRowNoFile()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var png = MediaImageFactory.GradientPng(32, 32, seed: 5);
        var truncated = png[..(png.Length / 2)];

        using var form = MultipartOf(("broken.png", truncated));
        var response = await client.PostAsync("/api/v1/media", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("تالف", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Upload_Gif_400_Unsupported()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x02, 0x00, 0x02, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00, 0x03, 0x08 };

        using var form = MultipartOf(("anim.gif", gif));
        var response = await client.PostAsync("/api/v1/media", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("غير مدعوم", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Upload_HeaderBomb_Claimed30000x30000_400_BeforeDecode()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        using var form = MultipartOf(("bomb.png", MediaImageFactory.HeaderOnlyBomb()));
        var response = await client.PostAsync("/api/v1/media", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("أبعاد", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Upload_TooLarge_400()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var big = new byte[10 * 1024 * 1024 + 1024]; // فوق 10MB
        big[0] = 0x89; big[1] = 0x50; big[2] = 0x4E; big[3] = 0x47; // ترويسة PNG سليمة
        using var form = MultipartOf(("huge.png", big));
        var response = await client.PostAsync("/api/v1/media", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ══ الربط والرئيسية وفك الربط ═════════════════════════════════════════

    [Fact]
    public async Task Link_ProductAndSku_ZeroOneManyImages_ExactPrimarySwap()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var product = await MediaApiFixture.GetProductAsync();
        var bagSku = await MediaApiFixture.GetSkuIdAsync("G5-MEDIA-01-BAG");

        // 0 صورة: NONE (Placeholder §6.3)
        var none = await client.GetFromJsonAsync<JsonElement>($"/api/v1/catalog/skus/{bagSku}/default-image");
        Assert.Equal("NONE", none.GetProperty("source").GetString());

        // صورة واحدة على المنتج → PRODUCT لكلتيهما (لا صورة مستوى الصيغة)
        var imgProductLevel = await _fx.UploadAsync(MediaImageFactory.GradientPng(50, 50, seed: 101));
        var link1 = await client.PostAsJsonAsync($"/api/v1/media/{imgProductLevel}/links",
            new { productId = product, makePrimary = true });
        Assert.Equal(HttpStatusCode.Created, link1.StatusCode);

        foreach (var sku in new[] { bagSku, await MediaApiFixture.GetSkuIdAsync("G5-MEDIA-01-BOX") })
        {
            var byProduct = await client.GetFromJsonAsync<JsonElement>($"/api/v1/catalog/skus/{sku}/default-image");
            Assert.Equal("PRODUCT", byProduct.GetProperty("source").GetString());
        }

        // عدة صور: صورة مستوى الصيغة تغلب على صورة المنتج لنفس الصيغة فقط
        var img1 = await _fx.UploadAsync(MediaImageFactory.GradientPng(60, 60, seed: 201));
        var img2 = await _fx.UploadAsync(MediaImageFactory.GradientPng(70, 70, seed: 202));

        var linkSku1 = await client.PostAsJsonAsync($"/api/v1/media/{img1}/links", new { skuId = bagSku });
        Assert.Equal(HttpStatusCode.Created, linkSku1.StatusCode);
        var linkSku2 = await client.PostAsJsonAsync($"/api/v1/media/{img2}/links",
            new { skuId = bagSku, makePrimary = true });
        Assert.Equal(HttpStatusCode.Created, linkSku2.StatusCode);

        var bagDefault = await client.GetFromJsonAsync<JsonElement>($"/api/v1/catalog/skus/{bagSku}/default-image");
        Assert.Equal("SKU", bagDefault.GetProperty("source").GetString());
        Assert.Equal(img2, bagDefault.GetProperty("mediaId").GetInt64()); // الرئيسية الجديدة تغلب

        // تبديل الرئيسية على مستوى الصيغة: واحدة فقط رئيسية
        var linkId1 = (await linkSku1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("linkId").GetInt64();
        var setPrimary = await client.PostAsync($"/api/v1/media/links/{linkId1}/set-primary", null);
        Assert.Equal(HttpStatusCode.OK, setPrimary.StatusCode);

        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var prim = new NpgsqlCommand(
            "SELECT COUNT(*) FROM catalog.product_images WHERE sku_id=@s AND is_primary", conn);
        prim.Parameters.AddWithValue("s", bagSku);
        Assert.Equal(1L, Convert.ToInt64(await prim.ExecuteScalarAsync())); // فهرس V002 الفريد حارساً

        // فك الربط لا يحذف الأصل إطلاقاً (§4.4 حـ)
        var links = await client.GetFromJsonAsync<JsonElement>($"/api/v1/media/{img1}/links");
        var linkIdImg1 = links[0].GetProperty("linkId").GetInt64();
        var unlink = await client.DeleteAsync($"/api/v1/media/links/{linkIdImg1}");
        Assert.Equal(HttpStatusCode.OK, unlink.StatusCode);

        var stillThere = await client.GetAsync($"/api/v1/media/{img1}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
        using var original = await client.GetAsync($"/api/v1/media/{img1}/content/original");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
    }

    [Fact]
    public async Task Link_BothTargets_400_OneTargetOnly()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var product = await MediaApiFixture.GetProductAsync();
        var bagSku = await MediaApiFixture.GetSkuIdAsync("G5-MEDIA-01-BAG");
        var media = await _fx.UploadAsync(MediaImageFactory.GradientPng(20, 20, seed: 301));

        var both = await client.PostAsJsonAsync($"/api/v1/media/{media}/links",
            new { productId = product, skuId = bagSku });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Contains("واحداً فقط", (await both.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var neither = await client.PostAsJsonAsync($"/api/v1/media/{media}/links", new { });
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);
    }

    [Fact]
    public async Task Link_SameMediaSameTargetTwice_409()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var bagSku = await MediaApiFixture.GetSkuIdAsync("G5-MEDIA-01-BAG");
        var media = await _fx.UploadAsync(MediaImageFactory.GradientPng(25, 25, seed: 401));

        var first = await client.PostAsJsonAsync($"/api/v1/media/{media}/links", new { skuId = bagSku });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/media/{media}/links", new { skuId = bagSku });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("مسبقاً", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Link_UnknownTarget_404_AndUnknownMedia_404()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var media = await _fx.UploadAsync(MediaImageFactory.GradientPng(15, 15, seed: 501));

        var unknownTarget = await client.PostAsJsonAsync($"/api/v1/media/{media}/links", new { skuId = 999_999 });
        Assert.Equal(HttpStatusCode.NotFound, unknownTarget.StatusCode);

        var unknownMedia = await client.PostAsJsonAsync("/api/v1/media/999999/links", new { skuId = await MediaApiFixture.GetSkuIdAsync("G5-MEDIA-01-BAG") });
        Assert.Equal(HttpStatusCode.NotFound, unknownMedia.StatusCode);
    }

    // ══ الحذف المحمي بالمراجعة ═════════════════════════════════════════════

    [Fact]
    public async Task DeleteOriginal_WhileLinked_409_CarriesReferences_ThenAfterUnlink_204_SweepsFiles()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var bagSku = await MediaApiFixture.GetSkuIdAsync("G5-MEDIA-01-BAG");
        var png = MediaImageFactory.GradientPng(33, 33, seed: 601);
        var media = await _fx.UploadAsync(png);

        await client.PostAsJsonAsync($"/api/v1/media/{media}/links", new { skuId = bagSku });

        // مربوط → الرفض يحمل قائمة الروابط (المراجعة الإلزامية)
        var refused = await client.DeleteAsync($"/api/v1/media/{media}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("مراجعة", body.GetProperty("detail").GetString());
        Assert.Equal(1, body.GetProperty("references").GetArrayLength());
        Assert.Equal("SKU", body.GetProperty("references")[0].GetProperty("targetType").GetString());

        // فك الربط ثم الحذف ينجح وينظف الملفات من القرص
        var linkId = body.GetProperty("references")[0].GetProperty("linkId").GetInt64();
        await client.DeleteAsync($"/api/v1/media/links/{linkId}");

        var sha = System.Security.Cryptography.SHA256.HashData(png);
        var ext = "png";
        var origKey = $"media/{Convert.ToHexString(sha)[..2].ToLowerInvariant()}/{Convert.ToHexString(sha).ToLowerInvariant()}/orig.{ext}";
        Assert.True(File.Exists(Path.Combine(_fx.Factory.MediaRoot, origKey.Replace('/', Path.DirectorySeparatorChar))), "الملف الأصلي مفقود قبل الحذف");

        var deleted = await client.DeleteAsync($"/api/v1/media/{media}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.False(File.Exists(Path.Combine(_fx.Factory.MediaRoot, origKey.Replace('/', Path.DirectorySeparatorChar))), "الملف الأصلي لم يُكنس بعد الحذف");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/media/{media}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/media/{media}/content/original")).StatusCode);
    }

    [Fact]
    public async Task ReprocessThumbnails_200_BumpsProcessedAt()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var media = await _fx.UploadAsync(MediaImageFactory.GradientPng(45, 45, seed: 701));

        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        long Before()
        {
            using var cmd = new NpgsqlCommand(
                "SELECT EXTRACT(EPOCH FROM processed_at)::bigint FROM catalog.media_assets WHERE media_id=@m", conn);
            cmd.Parameters.AddWithValue("m", media);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        var before = Before();
        await Task.Delay(1100); // ضمان اختلاف الطابع الزمني بالثانية
        var re = await client.PostAsync($"/api/v1/media/{media}/reprocess-thumbnails", null);
        Assert.Equal(HttpStatusCode.OK, re.StatusCode);
        Assert.True(Before() > before, "processed_at لم يتحدث");
    }

    // ══ الاستيراد الدفعي ═══════════════════════════════════════════════════

    [Fact]
    public async Task ImportJob_MixedComposition_Completes_ExactReport_SweptStaging()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");

        // أصل موجود مسبقاً في المكتبة (لاقطة التكرر مع المكتبة)
        var existingSha = MediaImageFactory.GradientPng(28, 28, seed: 801);
        await _fx.UploadAsync(existingSha);

        var job = await _fx.CreateJobAsync("استيراد G5 المختلط");
        var add = await _fx.AddFilesAsync(job, new (string, byte[])[]
        {
            ("unique-1.png", MediaImageFactory.GradientPng(30, 30, seed: 811)),
            ("unique-2.png", MediaImageFactory.GradientPng(31, 31, seed: 812)),
            ("unique-3.png", MediaImageFactory.GradientPng(32, 32, seed: 813)),
            ("unique-4.png", MediaImageFactory.GradientPng(33, 33, seed: 814)),
            ("dup-in-batch.png", MediaImageFactory.GradientPng(30, 30, seed: 811)),   // تكرر داخل الدفعة
            ("dup-of-library.png", existingSha),                                       // تكرر مع المكتبة
            ("corrupt-at-add-truncated.png", MediaImageFactory.Truncated(MediaImageFactory.GradientPng(34, 34, seed: 815))),
            ("corrupt-at-decode.png", MediaImageFactory.GarbageIdat(MediaImageFactory.GradientPng(35, 35, seed: 816))),
            ("reject-at-add.gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF }),
        });

        // 7 PENDING + 2 FAILED عند الإضافة (GIF + المبتور — الفاحص البنيوي يرفض البتر عند الإضافة)
        Assert.Equal(9, add.GetProperty("rows").GetArrayLength());
        var rejectedAtAdd = false;
        foreach (var row in add.GetProperty("rows").EnumerateArray())
        {
            if (row.GetProperty("state").GetString() == "FAILED"
                && row.GetProperty("errorCode").GetString() == "UNSUPPORTED_MEDIA_TYPE")
            {
                rejectedAtAdd = true;
            }
        }
        Assert.True(rejectedAtAdd, "ملف GIF لم يُرفض عند الإضافة");

        await _fx.StartJobAsync(job);
        var status = await _fx.PollUntilCompletedAsync(job, TimeSpan.FromSeconds(90));

        Assert.Equal("COMPLETED", status.GetProperty("state").GetString());
        var counts = status.GetProperty("counts");
        Assert.Equal(4, counts.GetProperty("done").GetInt32());
        Assert.Equal(2, counts.GetProperty("skipped").GetInt32());
        Assert.Equal(3, counts.GetProperty("failed").GetInt32());
        Assert.Equal(0, counts.GetProperty("pending").GetInt32());

        // تقرير لكل ملف: المقطوعة تذكر الأصل الموجود (EXACT_DUPLICATE_OF)
        var files = await client.GetFromJsonAsync<JsonElement>($"/api/v1/media/import-jobs/{job}/files?state=SKIPPED");
        Assert.Equal(2, files.GetProperty("totalCount").GetInt64());
        var skipReasonMentionsExisting = false;
        foreach (var f in files.GetProperty("items").EnumerateArray())
        {
            if (f.GetProperty("skipReason").GetString()!.StartsWith("EXACT_DUPLICATE_OF:", StringComparison.Ordinal))
            {
                skipReasonMentionsExisting = true;
            }
        }
        Assert.True(skipReasonMentionsExisting, "سبب SKIPPED لا يذكر الأصل الموجود");

        var report = await client.GetFromJsonAsync<JsonElement>($"/api/v1/media/import-jobs/{job}/report");
        var errors = report.GetProperty("errorsByCode");
        Assert.Equal(1, errors.GetProperty("UNSUPPORTED_MEDIA_TYPE").GetInt32());
        Assert.Equal(2, errors.GetProperty("CORRUPT_IMAGE").GetInt32()); // مبتور عند الإضافة + مهترئ عند الفك

        // staging كُنس عند الإقفال — ولا يزال محتوى المكتبة سليماً
        Assert.False(Directory.Exists(Path.Combine(_fx.Factory.MediaRoot, "staging", job.ToString("D10"))), "لم يُكنس staging");
        using (var check = await client.GetAsync($"/api/v1/catalog/skus/{await MediaApiFixture.GetSkuIdAsync("G5-MEDIA-01-BAG")}/default-image"))
        {
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        }

        // 4 أصول جديدة دخلت المكتبة فعلاً
        var library = await client.GetFromJsonAsync<JsonElement>("/api/v1/media?linkState=UNLINKED&pageSize=50");
        Assert.True(library.GetProperty("totalCount").GetInt64() >= 4);

        // تدقيق الإقفال مكتوب
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var audit = new NpgsqlCommand(
            "SELECT COUNT(*) FROM audit.audit_logs WHERE entity_schema='catalog.media_import_jobs' AND entity_id=@j AND action='MEDIA_IMPORT_COMPLETED'", conn);
        audit.Parameters.AddWithValue("j", job);
        Assert.True(Convert.ToInt64(await audit.ExecuteScalarAsync()) >= 1);
    }

    [Fact]
    public async Task ImportJob_CancelThenResume_Completes_StagingKeptUntilCompletion()
    {
        using var client = await ClientAsync("g5.storekeeper", "Storekeeper#G5-Pass"); // الاستيراد للمخازني أيضاً (V016)
        var job = await _fx.CreateJobAsync("إلغاء ثم استئناف");

        var files = Enumerable.Range(0, 300)
            .Select(i => ($"tiny-{i}.png", MediaImageFactory.GradientPng(9, 9, seed: 1000 + i)))
            .ToArray();
        var addResult = await _fx.AddFilesAsync(job, files);
        Assert.Equal(300, addResult.GetProperty("rows").GetArrayLength()); // تشخيص: هل اقتُطع الـmultipart؟
        await _fx.StartJobAsync(job);

        // إلغاء آمن: يتوقف الالتقاط — لا يُلقى أي ملف
        await Task.Delay(300); // دع العامل يبدأ (0..عدة ملفات)
        var cancelled = await client.PostAsync($"/api/v1/media/import-jobs/{job}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var cancelledStatus = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CANCELLED", cancelledStatus.GetProperty("state").GetString());

        var countsAfterCancel = cancelledStatus.GetProperty("counts");
        var accounted = countsAfterCancel.GetProperty("pending").GetInt32()
            + countsAfterCancel.GetProperty("done").GetInt32()
            + countsAfterCancel.GetProperty("processing").GetInt32()
            + countsAfterCancel.GetProperty("failed").GetInt32()
            + countsAfterCancel.GetProperty("skipped").GetInt32();
        Assert.Equal(300, accounted); // لا ملف مفقود ولا مكرر

        // staging لم يُكنس بعد الإلغاء (الاستئناف يحتاج البايتات)
        Assert.True(Directory.Exists(Path.Combine(_fx.Factory.MediaRoot, "staging", job.ToString("D10"))));

        // الاستئناف من CANCELLED → إقفال كامل
        await _fx.StartJobAsync(job);
        var status = await _fx.PollUntilCompletedAsync(job, TimeSpan.FromSeconds(120));
        Assert.Equal("COMPLETED", status.GetProperty("state").GetString());
        var finalCounts = status.GetProperty("counts");
        Assert.True(
            finalCounts.GetProperty("pending").GetInt32() == 0
            && finalCounts.GetProperty("done").GetInt32() + finalCounts.GetProperty("skipped").GetInt32() == 300,
            "العدادات النهائية: " + finalCounts.GetRawText() + " | report=" + client.GetFromJsonAsync<JsonElement>($"/api/v1/media/import-jobs/{job}/report").GetAwaiter().GetResult().GetRawText());
    }

    [Fact]
    public async Task ImportJob_StaleProcessingHealed_Completes()
    {
        var job = await _fx.CreateJobAsync("تعافي انهيار");
        await _fx.AddFilesAsync(job, new (string, byte[])[]
        {
            ("a.png", MediaImageFactory.GradientPng(12, 12, seed: 2101)),
            ("b.png", MediaImageFactory.GradientPng(13, 13, seed: 2102)),
            ("c.png", MediaImageFactory.GradientPng(14, 14, seed: 2103)),
        });

        // محاكاة انهيار: ملف عالق PROCESSING والمهمة ما زالت OPEN
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var stale = new NpgsqlCommand(
            "UPDATE catalog.media_import_files SET state='PROCESSING' WHERE job_id=@j AND ordinal=2", conn);
        stale.Parameters.AddWithValue("j", job);
        Assert.Equal(1, await stale.ExecuteNonQueryAsync());

        await _fx.StartJobAsync(job);
        var status = await _fx.PollUntilCompletedAsync(job, TimeSpan.FromSeconds(90));

        // الملف «العالق» عُوجل وأُكملت المهمة — لا انسداد أبداً
        Assert.Equal("COMPLETED", status.GetProperty("state").GetString());
        Assert.Equal(0, status.GetProperty("counts").GetProperty("pending").GetInt32());
        Assert.Equal(0, status.GetProperty("counts").GetProperty("processing").GetInt32());
        Assert.Equal(3, status.GetProperty("counts").GetProperty("done").GetInt32());
    }

    [Fact]
    public async Task ImportJob_AddToRunning_409_AndTerminalTransitionsRejected()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var job = await _fx.CreateJobAsync("انتقالات مرفوضة");
        await _fx.AddFilesAsync(job, new[] { ("only.png", MediaImageFactory.GradientPng(10, 10, seed: 2201)) });
        await _fx.StartJobAsync(job);

        // إضافة على RUNNING → 409
        var addWhileRunning = await _fx.AddFilesRawAsync(job, new[] { ("late.png", MediaImageFactory.GradientPng(11, 11, seed: 2202)) });
        Assert.Equal(HttpStatusCode.Conflict, addWhileRunning.StatusCode);

        await _fx.PollUntilCompletedAsync(job, TimeSpan.FromSeconds(60));

        // إقفال مزدوج: تشغيل مكتملة → 409؛ إلغاء مكتملة → 409
        var restart = await client.PostAsync($"/api/v1/media/import-jobs/{job}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, restart.StatusCode);
        var recancel = await client.PostAsync($"/api/v1/media/import-jobs/{job}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, recancel.StatusCode);
    }

    // ══ RBAC خادمي ═════════════════════════════════════════════════════════

    [Fact]
    public async Task RBAC_CashierReadsButCannotManageOrImport_ManagerManagesButNoImport_StorekeeperFullMedia()
    {
        // الكاشير: يقرأ المكتبة (V016) لكن لا يرفع ولا يربط ولا يستورد
        using (var cashier = await ClientAsync("g5.cashier", "Cashier#G5-Pass"))
        {
            Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/v1/media?pageSize=5")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/v1/media/duplicates")).StatusCode);

            using var form = MultipartOf(("c.png", MediaImageFactory.GradientPng(9, 9, seed: 2301)));
            Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsync("/api/v1/media", form)).StatusCode);

            var bagSku = await MediaApiFixture.GetSkuIdAsync("G5-MEDIA-01-BAG");
            Assert.Equal(HttpStatusCode.Forbidden,
                (await cashier.PostAsJsonAsync($"/api/v1/media/999999/links", new { skuId = bagSku })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await cashier.PostAsync("/api/v1/media/import-jobs", null)).StatusCode);
        }

        // المدير: يدير الوسائط (PAGE-16 م، مش، خ) لكن لا يستورد (PAGE-17 م، خ)
        using (var manager = await ClientAsync("g5.manager", "Manager#G5-Pass"))
        {
            using var form = MultipartOf(("m.png", MediaImageFactory.GradientPng(9, 9, seed: 2302)));
            Assert.Equal(HttpStatusCode.Created, (await manager.PostAsync("/api/v1/media", form)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync("/api/v1/media/import-jobs", null)).StatusCode);
        }

        // أمين المخزن: يدير ويستورد (V016) لكن لا ينشئ منتجات (V009)
        using (var keeper = await ClientAsync("g5.storekeeper", "Storekeeper#G5-Pass"))
        {
            using var form = MultipartOf(("k.png", MediaImageFactory.GradientPng(9, 9, seed: 2303)));
            Assert.Equal(HttpStatusCode.Created, (await keeper.PostAsync("/api/v1/media", form)).StatusCode);
            var job = await keeper.PostAsJsonAsync("/api/v1/media/import-jobs", new { label = "مهمة المخازني" });
            Assert.Equal(HttpStatusCode.Created, job.StatusCode);
        }
    }

    [Fact]
    public async Task Anonymous_401_OnAllMediaEndpoints()
    {
        using var anon = _fx.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/media")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/media/duplicates")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/v1/media", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/v1/media/import-jobs", null)).StatusCode);
    }

    // ══ فحص بصمة الشبكة (POS لا يتوقف بسبب الصور §6.3) ══════════════════════

    [Fact]
    public async Task MissingVariant_404_AndUnknownVariant_400_NeverA500()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        // وسط غير موجود: 404 نصي لا 500 بتتبع
        var missing = await client.GetAsync("/api/v1/media/999999/content/thumb-pos");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var media = await _fx.UploadAsync(MediaImageFactory.GradientPng(18, 18, seed: 2401));
        var unknownVariant = await client.GetAsync($"/api/v1/media/{media}/content/mega-huge");
        Assert.Equal(HttpStatusCode.BadRequest, unknownVariant.StatusCode);
    }

    [Fact]
    public async Task Search_PagedWithLinkStateFilter_TotalFromRealQuery()
    {
        using var client = await ClientAsync("g5.owner", "Owner#G5-Pass");
        var bagSku = await MediaApiFixture.GetSkuIdAsync("G5-MEDIA-01-BAG");

        // ربط واحدة على الأقل لصيغة، ثم فلترة LINKED/UNLINKED
        var linked = await _fx.UploadAsync(MediaImageFactory.GradientPng(21, 21, seed: 2501));
        await client.PostAsJsonAsync($"/api/v1/media/{linked}/links", new { skuId = bagSku });

        var unlinkedOnly = await client.GetFromJsonAsync<JsonElement>("/api/v1/media?linkState=UNLINKED&pageSize=50");
        var linkedOnly = await client.GetFromJsonAsync<JsonElement>("/api/v1/media?linkState=LINKED&pageSize=50");
        Assert.True(linkedOnly.GetProperty("totalCount").GetInt64() >= 1);
        foreach (var item in linkedOnly.GetProperty("items").EnumerateArray())
        {
            Assert.True(item.GetProperty("linkCount").GetInt64() >= 1);
        }
        foreach (var item in unlinkedOnly.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(0, item.GetProperty("linkCount").GetInt64());
        }

        // صفحة أكبر من 50 تُقصّ (نمط G4)
        var capped = await client.GetFromJsonAsync<JsonElement>("/api/v1/media?pageSize=500");
        Assert.True(capped.GetProperty("pageSize").GetInt32() <= 50);
    }
}

/// <summary>مصنع صور اختبارية حقيقية (PNG بتدرجات مختلفة لكل seed).</summary>
public static class MediaImageFactory
{
    public static byte[] GradientPng(int width, int height, int seed = 0)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var canvas = surface.Canvas;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = (byte)((x * 7 + seed) % 256);
                var g = (byte)((y * 5 + seed * 3) % 256);
                var b = (byte)((x * y + seed) % 256);
                canvas.DrawRect(x, y, 1, 1, new SKPaint { Color = new SKColor(r, g, b) });
            }
        }
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    public static byte[] Truncated(byte[] source) => source[..(source.Length / 2)];

    /// <summary>بنية PNG سليمة مع بيانات IDAT مهترئة فقط (بلا مساس بالأطوال) — يُرفض عند الفك لا عند التطفل.</summary>
    public static byte[] GarbageIdat(byte[] source)
    {
        var clone = (byte[])source.Clone();
        long position = 8;
        while (position + 12 <= clone.Length)
        {
            var length = (clone[position] << 24) | (clone[position + 1] << 16) | (clone[position + 2] << 8) | clone[position + 3];
            var type = System.Text.Encoding.ASCII.GetString(clone, (int)position + 4, 4);
            if (type == "IDAT")
            {
                var start = (int)position + 8;
                var end = Math.Min((int)position + 8 + length, clone.Length - 4);
                for (var i = start; i < end; i++)
                {
                    clone[i] = 0xAA;
                }
                return clone;
            }
            position += 12 + length;
        }
        throw new InvalidOperationException("لا توجد قطعة IDAT في PNG الاختباري");
    }

    /// <summary>PNG بترويسة تدّعي 30000×30000 وجسم شبه فارغ — قنبلة الفك.</summary>
    public static byte[] HeaderOnlyBomb()
    {
        var head = new List<byte>
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D,
        };
        head.AddRange("IHDR"u8.ToArray());
        head.AddRange([0x00, 0x00, 0x75, 0x30]);
        head.AddRange([0x00, 0x00, 0x75, 0x30]);
        head.AddRange([8, 6, 0, 0, 0]);
        head.AddRange(new byte[8]);
        head.AddRange("IEND"u8.ToArray());
        return [.. head];
    }
}

/// <summary>
/// تجهيز G5: مستخدمون بأربعة أدوار + منتج السيناريو (كيس/علبة) عبر الـAPI
/// نفسه + تنظيف كامل بترتيب يحترم الفواصل (media_import_files تتتالي مع
/// المهمة؛ product_images قبل media_assets؛ والمستخدمون آخراً).
/// </summary>
public sealed class MediaApiFixture : IAsyncLifetime
{
    public WeraApiFactory Factory { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Client = Factory.CreateClient(); // يقلع ويرحّل حتى رأس V016
        await SeedAsync();
    }

    public HttpClient CreateClient() => Factory.CreateClient();

    public async Task<Contracts.V1.LoginResponse> LoginAsync(string username, string password)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password, branchId = (long?)null, stationId = (long?)null });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Contracts.V1.LoginResponse>())!;
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();

        long Scalar(string sql)
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        void UpsertUser(string username, string display, string password, string roleCode)
        {
            var hash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);
            Scalar($"INSERT INTO core.users (username, display_name_ar, password_hash, is_active) VALUES ('{username}', '{display}', '{hash}', true) ON CONFLICT (username) DO UPDATE SET password_hash = EXCLUDED.password_hash");
            var userId = Scalar($"SELECT user_id FROM core.users WHERE username = '{username}'");
            Scalar($"DELETE FROM core.user_roles WHERE user_id = {userId}");
            Scalar($"INSERT INTO core.user_roles (user_id, role_id, branch_id) VALUES ({userId}, (SELECT role_id FROM core.roles WHERE code='{roleCode}'), NULL)");
        }

        UpsertUser("g5.owner", "مالك G5", "Owner#G5-Pass", "OWNER");
        UpsertUser("g5.manager", "مدير G5", "Manager#G5-Pass", "MANAGER");
        UpsertUser("g5.storekeeper", "أمين مخزن G5", "Storekeeper#G5-Pass", "STOREKEEPER");
        UpsertUser("g5.cashier", "كاشير G5", "Cashier#G5-Pass", "CASHIER");

        var categoryId = Scalar("INSERT INTO catalog.categories (code, name_ar) VALUES ('G5CAT', 'فئة اختبار G5') ON CONFLICT (code) DO UPDATE SET name_ar = EXCLUDED.name_ar RETURNING category_id");
        var priceListId = Scalar("SELECT price_list_id FROM catalog.price_lists WHERE code='DEFAULT_EGP'");

        var payload = CatalogEndpointTests.BagAndBoxPayload(
            "G5-MEDIA-01", "G5-MEDIA-01-BAG", "G5-MEDIA-01-BOX",
            "6221031492242", "6221031492396", categoryId, priceListId);
        var login = await LoginAsync("g5.owner", "Owner#G5-Pass");
        using var authed = CreateClient();
        authed.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);
        var created = await authed.PostAsJsonAsync("/api/v1/catalog/products", payload);
        Assert.True(created.IsSuccessStatusCode, "بذر منتج الوسائط فشل: " + await created.Content.ReadAsStringAsync());
    }

    public static async Task<long> GetSkuIdAsync(string skuCode)
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT sku_id FROM catalog.skus WHERE sku_code = @c", conn);
        cmd.Parameters.AddWithValue("c", skuCode);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static async Task<long> GetProductAsync()
    {
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT product_id FROM catalog.products WHERE code = 'G5-MEDIA-01'", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>رفع مباشر عبر API المالك — يعيد mediaId.</summary>
    public async Task<long> UploadAsync(byte[] png, string name = "seed.png")
    {
        var login = await LoginAsync("g5.owner", "Owner#G5-Pass");
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

        var content = new MultipartFormDataContent();
        var part = new StreamContent(new MemoryStream(png));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, "file", name);

        var response = await client.PostAsync("/api/v1/media", content);
        Assert.True(response.IsSuccessStatusCode, "رفع البذر فشل: " + await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("media").GetProperty("mediaId").GetInt64();
    }

    public async Task<long> CreateJobAsync(string label)
    {
        var login = await LoginAsync("g5.owner", "Owner#G5-Pass");
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);
        var response = await client.PostAsJsonAsync("/api/v1/media/import-jobs", new { label });
        Assert.True(response.IsSuccessStatusCode, "إنشاء المهمة فشل: " + await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetInt64();
    }

    public async Task<JsonElement> AddFilesAsync(long jobId, (string Name, byte[] Bytes)[] files)
    {
        var response = await AddFilesRawAsync(jobId, files);
        Assert.True(response.IsSuccessStatusCode, "إضافة الملفات فشلت: " + await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<HttpResponseMessage> AddFilesRawAsync(long jobId, (string Name, byte[] Bytes)[] files)
    {
        var login = await LoginAsync("g5.owner", "Owner#G5-Pass");
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

        var content = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new StreamContent(new MemoryStream(bytes));
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(part, "files", name);
        }
        return await client.PostAsync($"/api/v1/media/import-jobs/{jobId}/files", content);
    }

    public async Task StartJobAsync(long jobId)
    {
        var login = await LoginAsync("g5.owner", "Owner#G5-Pass");
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);
        var response = await client.PostAsync($"/api/v1/media/import-jobs/{jobId}/start", null);
        Assert.True(response.IsSuccessStatusCode, "تشغيل المهمة فشل: " + await response.Content.ReadAsStringAsync());
    }

    public async Task<JsonElement> PollUntilCompletedAsync(long jobId, TimeSpan timeout)
    {
        var login = await LoginAsync("g5.owner", "Owner#G5-Pass");
        using var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/media/import-jobs/{jobId}");
            if (status.GetProperty("state").GetString() is "COMPLETED" or "CANCELLED")
            {
                return status;
            }
            await Task.Delay(400);
        }
        throw new TimeoutException($"المهمة {jobId} لم تكتمل خلال {timeout.TotalSeconds:0}s");
    }

    public async Task DisposeAsync()
    {
        // ترتيب يحترم الفواصل: الروابط قبل الأصول؛ ملفات الاستيراد تتتالي مع مهامها؛
        // ثم منتجات/تجهيزات G5 وأخيراً التدقيق والجلسات والمستخدمون.
        await using var conn = new NpgsqlConnection(WeraApiFactory.TestDbUri);
        await conn.OpenAsync();
        await using var cleanup = new NpgsqlCommand(
            "DELETE FROM catalog.product_images WHERE media_id IN (SELECT media_id FROM catalog.media_assets); " +
            "DELETE FROM catalog.media_import_jobs; " +   // تتتالي الملفات (ON DELETE CASCADE)
            "DELETE FROM catalog.media_assets; " +
            "DELETE FROM catalog.barcodes WHERE sku_id IN (SELECT sku_id FROM catalog.skus WHERE sku_code LIKE 'G5-MEDIA-%'); " +
            "DELETE FROM catalog.plus WHERE sku_id IN (SELECT sku_id FROM catalog.skus WHERE sku_code LIKE 'G5-MEDIA-%'); " +
            "DELETE FROM catalog.product_prices WHERE sku_id IN (SELECT sku_id FROM catalog.skus WHERE sku_code LIKE 'G5-MEDIA-%'); " +
            "DELETE FROM catalog.packages WHERE package_sku_id IN (SELECT sku_id FROM catalog.skus WHERE sku_code LIKE 'G5-MEDIA-%') OR content_sku_id IN (SELECT sku_id FROM catalog.skus WHERE sku_code LIKE 'G5-MEDIA-%'); " +
            "DELETE FROM catalog.skus WHERE sku_code LIKE 'G5-MEDIA-%'; " +
            "DELETE FROM catalog.products WHERE code LIKE 'G5-MEDIA-%'; " +
            "DELETE FROM core.refresh_tokens WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g5.%'); " +
            "DELETE FROM audit.user_sessions WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g5.%'); " +
            "DELETE FROM audit.audit_logs WHERE entity_schema IN ('catalog.media_assets','catalog.product_images','catalog.media_import_jobs'); " +
            "DELETE FROM audit.audit_logs WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g5.%'); " +
            "DELETE FROM catalog.categories WHERE code = 'G5CAT'; " +
            "DELETE FROM core.user_roles WHERE user_id IN (SELECT user_id FROM core.users WHERE username LIKE 'g5.%'); " +
            "DELETE FROM core.users WHERE username LIKE 'g5.%'; ", conn);
        await cleanup.ExecuteNonQueryAsync();
        Factory.Dispose();
        if (Directory.Exists(Factory.MediaRoot))
        {
            try { Directory.Delete(Factory.MediaRoot, recursive: true); }
            catch (IOException) { /* أفضل جهد */ }
        }
    }
}
