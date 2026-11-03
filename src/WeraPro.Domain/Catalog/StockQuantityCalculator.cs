namespace WeraPro.Domain.Catalog;

using WeraPro.Domain.SharedKernel;

/// <summary>
/// لقطة التحويل التي تُختم في صف الدفتر (inv.stock_movements): الكمية بالوحدة
/// الأساسية + وحدة المستند الأصلي + عامل التحويل وقت الحركة (§4.3 ق9: التعديل
/// اللاحق لتعريف العبوة لا يعيد حساب التاريخ).
/// </summary>
public sealed record StockQuantitySnapshot(
    long SkuId,                  // SKU التي يقع عليها الأثر (المحتوى عند PACK)
    decimal BaseQty,             // بالوحدة الأساسية — موجبة دائماً (الاتجاه في direction)
    long SourceUomId,            // وحدة المستند الأصلي
    decimal ConversionFactor);   // لقطة العامل: الكمية بالمصدر × العامل = base_qty

/// <summary>بيانات SKU اللازمة لحساب الكمية (من catalog.skus).</summary>
public sealed record SkuSaleInfo(
    long SkuId,
    long ProductId,
    string SkuCode,
    SaleMode SaleMode,
    long BaseUomId,
    long SaleUomId,
    decimal? MinWeightKg,
    decimal? MaxWeightKg);

/// <summary>تعريف العبوة (catalog.packages) — صيغة البيع PACK تخصم من رصيد المحتوى.</summary>
public sealed record PackageInfo(
    long PackageSkuId,
    long ContentSkuId,
    decimal UnitsPerPackage,
    bool OpenPackageAllowed);

/// <summary>
/// حاسبة كمية المخزون (§4.2–4.3): تحويل كمية المستند إلى الوحدة الأساسية لكل
/// صيغة بيع — أثر مخزون واحد مهما اختلف شكل البيع، بلقطة عامل صريحة:
/// COUNT: الكمية بوحدة البيع × عامل التحويل إلى الأساسية.
/// WEIGHT: الوزن بوحدة MASS يُحوَّل إلى الوحدة الأساسية (كجم/جرام) داخل البعد.
/// PACK: الكمية × units_per_package من رصيد المحتوى — صف واحد على المحتوى.
/// SERIALIZED: وحدة لكل سيريال — الكمية عدد صحيح يساوي عدد السيريالات.
/// </summary>
public static class StockQuantityCalculator
{
    /// <summary>صيغة COUNT — كمية بعدد وحدات البيع تُحوَّل إلى الوحدة الأساسية.</summary>
    public static Result<StockQuantitySnapshot> ResolveCount(
        SkuSaleInfo sku,
        decimal quantity,
        long sourceUomId,
        UnitConversionGraph conversions)
    {
        if (quantity <= 0)
        {
            return Result.Fail<StockQuantitySnapshot>(new("QTY_NOT_POSITIVE", "الكمية يجب أن تكون موجبة"));
        }
        if (sku.SaleMode is not (SaleMode.Count or SaleMode.Serialized))
        {
            return Result.Fail<StockQuantitySnapshot>(new(
                "SALE_MODE_MISMATCH",
                $"هذه الكمية ليست عدداً — صيغة الصنف {sku.SaleMode}"));
        }

        var factor = conversions.Factor(sourceUomId, sku.BaseUomId);
        if (!factor.IsSuccess)
        {
            return Result.Fail<StockQuantitySnapshot>(factor.Error!);
        }

        return Result.Ok(new StockQuantitySnapshot(
            sku.SkuId,
            decimal.Round(quantity * factor.Value!, 6, MidpointRounding.AwayFromZero),
            sourceUomId,
            factor.Value!));
    }

    /// <summary>
    /// صيغة WEIGHT — الوزن بوحدة كتلة (كجم أو جرام) يُحوَّل إلى الوحدة الأساسية
    /// داخل بُعد الكتلة؛ حدود الصنف مفروضة (min/max بالكيلوجرام) والوزن موجب دائماً.
    /// </summary>
    public static Result<StockQuantitySnapshot> ResolveWeight(
        SkuSaleInfo sku,
        decimal weight,
        long weightSourceUomId,
        UnitConversionGraph conversions)
    {
        if (sku.SaleMode != SaleMode.Weight)
        {
            return Result.Fail<StockQuantitySnapshot>(new(
                "SALE_MODE_MISMATCH",
                "هذا الصنف ليس وزنياً — استخدم الكمية العددية"));
        }
        if (weight <= 0)
        {
            return Result.Fail<StockQuantitySnapshot>(new(
                "WEIGHT_NOT_POSITIVE",
                "الوزن صفر أو سالب مرفوض (§5.1)"));
        }
        if (!conversions.HasUnit(weightSourceUomId) || !conversions.HasUnit(sku.BaseUomId))
        {
            return Result.Fail<StockQuantitySnapshot>(new("UNIT_UNKNOWN", "وحدة قياس غير معرفة في المخطط"));
        }
        if (conversions.Unit(weightSourceUomId).Dimension != UnitDimension.Mass
            || conversions.Unit(sku.BaseUomId).Dimension != UnitDimension.Mass)
        {
            return Result.Fail<StockQuantitySnapshot>(new(
                "UNIT_DIMENSION_MISMATCH",
                "الوزن يُستقبل بوحدات كتلة فقط (كجم/جرام) — والوحدة الأساسية للصنف الوزني يجب أن تكون كتلة"));
        }

        var factor = conversions.Factor(weightSourceUomId, sku.BaseUomId);
        if (!factor.IsSuccess)
        {
            return Result.Fail<StockQuantitySnapshot>(factor.Error!);
        }

        var baseQty = decimal.Round(weight * factor.Value!, 6, MidpointRounding.AwayFromZero);

        // حدود الصنف مفروضة بالكيلوجرام (min_weight_kg/max_weight_kg — §5.1)
        var kg = conversions.Units.FirstOrDefault(u => u.Code == "KG");
        if (kg is null)
        {
            return Result.Fail<StockQuantitySnapshot>(new(
                "KG_UNIT_REQUIRED",
                "وحدة الكيلوجرام غير معرفة — لا يمكن فرض حدود الوزن"));
        }
        var kgFactor = conversions.Factor(sku.BaseUomId, kg.UomId);
        if (!kgFactor.IsSuccess)
        {
            return Result.Fail<StockQuantitySnapshot>(kgFactor.Error!);
        }
        var weightKg = baseQty * kgFactor.Value!;
        if (sku.MinWeightKg is { } min && weightKg < min)
        {
            return Result.Fail<StockQuantitySnapshot>(new(
                "WEIGHT_BELOW_MIN",
                $"الوزن {weightKg:0.###} كجم أقل من الحد الأدنى للصنف ({min:0.###} كجم)"));
        }
        if (sku.MaxWeightKg is { } max && weightKg > max)
        {
            return Result.Fail<StockQuantitySnapshot>(new(
                "WEIGHT_ABOVE_MAX",
                $"الوزن {weightKg:0.###} كجم أعلى من الحد الأقصى للصنف ({max:0.###} كجم)"));
        }

        return Result.Ok(new StockQuantitySnapshot(sku.SkuId, baseQty, weightSourceUomId, factor.Value!));
    }

    /// <summary>
    /// صيغة PACK — بيع/شراء/إرجاع العبوة يخصم/يزيد units_per_package من رصيد
    /// المحتوى: أثر واحد على SKU المحتوى (§4.3 ق5) بعامل العبوة كاملاً.
    /// وحدة المصدر هي وحدة العبوة (عدد علب — بُعد COUNT) والعامل لقطة تعريفها.
    /// </summary>
    public static Result<StockQuantitySnapshot> ResolvePack(
        PackageInfo package,
        SkuSaleInfo contentSku,
        decimal packageQuantity,
        long packageSourceUomId)
    {
        if (packageQuantity <= 0)
        {
            return Result.Fail<StockQuantitySnapshot>(new("QTY_NOT_POSITIVE", "عدد العبوات يجب أن يكون موجباً"));
        }
        if (package.UnitsPerPackage <= 0)
        {
            return Result.Fail<StockQuantitySnapshot>(new(
                "PACKAGE_UNITS_INVALID",
                "عدد الوحدات داخل العبوة يجب أن يكون موجباً"));
        }
        if (package.ContentSkuId != contentSku.SkuId)
        {
            return Result.Fail<StockQuantitySnapshot>(new(
                "PACKAGE_CONTENT_MISMATCH",
                "تعريف العبوة لا يطابق صنف المحتوى المرسل"));
        }

        var baseQty = decimal.Round(packageQuantity * package.UnitsPerPackage, 6, MidpointRounding.AwayFromZero);
        return Result.Ok(new StockQuantitySnapshot(
            contentSku.SkuId,      // الأثر يقع على المحتوى (الكيس) لا على SKU العلبة
            baseQty,
            packageSourceUomId,    // وحدة المستند: عدد علب
            package.UnitsPerPackage));
    }

    /// <summary>صيغة SERIALIZED — وحدة لكل سيريال: الكمية عدد صحيح = عدد السيريالات.</summary>
    public static Result<StockQuantitySnapshot> ResolveSerialized(
        SkuSaleInfo sku,
        int serialCount)
    {
        if (serialCount <= 0)
        {
            return Result.Fail<StockQuantitySnapshot>(new(
                "SERIAL_COUNT_NOT_POSITIVE",
                "عدد السيريالات يجب أن يكون موجباً"));
        }
        return Result.Ok(new StockQuantitySnapshot(sku.SkuId, serialCount, sku.BaseUomId, 1m));
    }

    /// <summary>
    /// حساب موحّد حسب الصيغة — يفوض إلى الدالة المختصة. عند PACK يجب إرسال تعريف
    /// العبوة ومعلومات صنف المحتوى (الأثر يقع عليه)، وعند SERIALIZED قائمة السيريالات.
    /// </summary>
    public static Result<StockQuantitySnapshot> Resolve(
        SkuSaleInfo sku,
        decimal quantity,
        long sourceUomId,
        UnitConversionGraph conversions,
        PackageInfo? package = null,
        SkuSaleInfo? contentSku = null,
        IReadOnlyList<string>? serials = null)
    {
        return sku.SaleMode switch
        {
            SaleMode.Count => ResolveCount(sku, quantity, sourceUomId, conversions),
            SaleMode.Serialized => serials is null || serials.Count == 0
                ? Result.Fail<StockQuantitySnapshot>(new(
                    "SERIALS_REQUIRED",
                    "صيغة SERIALIZED توجب سيريالاً لكل وحدة"))
                : ResolveSerialized(sku, serials.Count),
            SaleMode.Pack when package is null => Result.Fail<StockQuantitySnapshot>(new(
                "PACKAGE_DEFINITION_REQUIRED",
                "صيغة PACK توجب تعريف عبوة")),
            SaleMode.Pack when contentSku is null => Result.Fail<StockQuantitySnapshot>(new(
                "PACKAGE_CONTENT_SKU_REQUIRED",
                "صيغة PACK توجب معلومات صنف المحتوى")),
            SaleMode.Pack => ResolvePack(package!, contentSku!, quantity, sourceUomId),
            SaleMode.Weight => ResolveWeight(sku, quantity, sourceUomId, conversions),
            _ => Result.Fail<StockQuantitySnapshot>(new("SALE_MODE_UNKNOWN", "صيغة بيع غير معروفة")),
        };
    }
}
