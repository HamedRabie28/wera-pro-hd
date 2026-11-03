namespace WeraPro.Domain.Catalog;

using WeraPro.Domain.SharedKernel;

/// <summary>تعريف وحدة قياس (V002 catalog.units).</summary>
public sealed record UnitDefinition(long UomId, string Code, UnitDimension Dimension, bool IsBase, int Decimals);

/// <summary>تعريف تحويل صريح (V002 catalog.unit_conversions): كمية المصدر × العامل = الكمية بالهدف.</summary>
public sealed record ConversionDefinition(long FromUomId, long ToUomId, decimal Factor);

/// <summary>
/// مخطط تحويل الوحدات (§4.1): التحويل داخل البُعد فقط، لا دورات، ولا تعارض
/// بين مسارات متعددة — الدورات المباشرة يمنعها مشغل القاعدة (tf_unit_conversion_no_cycle)،
/// والدورات الأطول وتعارض المسارات تمنعها هذه الخدمة بجولة الرسم البياني (T-CAT-006).
/// البناء يفشل صريحاً بأي خلل — لا تخمين ولا تجاهل.
/// </summary>
public sealed class UnitConversionGraph
{
    private readonly Dictionary<long, UnitDefinition> _units;
    private readonly List<ConversionDefinition> _conversions;
    private readonly Dictionary<long, List<ConversionDefinition>> _outgoing = [];
    private readonly Dictionary<long, List<ConversionDefinition>> _incoming = [];

    private UnitConversionGraph(
        Dictionary<long, UnitDefinition> units,
        List<ConversionDefinition> conversions)
    {
        _units = units;
        _conversions = conversions;
        foreach (var c in conversions)
        {
            _outgoing.TryAdd(c.FromUomId, []);
            _outgoing[c.FromUomId].Add(c);
            _incoming.TryAdd(c.ToUomId, []);
            _incoming[c.ToUomId].Add(c);
        }
    }

    public IReadOnlyList<UnitDefinition> Units => _units.Values.ToList();
    public IReadOnlyList<ConversionDefinition> Conversions => _conversions;

    /// <summary>
    /// بناء المخطط مع الفحص الكامل: وحدات موجودة · أبعاد متوافقة · عوامل موجبة ·
    /// لا حواف مكررة · لا دورات (DFS بألوان) · لا مسارين بعاملين مختلفين لأي زوج.
    /// </summary>
    public static Result<UnitConversionGraph> TryCreate(
        IReadOnlyList<UnitDefinition> units,
        IReadOnlyList<ConversionDefinition> conversions)
    {
        var unitMap = units.ToDictionary(u => u.UomId);
        foreach (var c in conversions)
        {
            if (!unitMap.TryGetValue(c.FromUomId, out var fromUnit) || !unitMap.TryGetValue(c.ToUomId, out var toUnit))
            {
                return Result.Fail<UnitConversionGraph>(new(
                    "UNIT_CONVERSION_UNKNOWN_UNIT",
                    $"التحويل {c.FromUomId}→{c.ToUomId} يشير لوحدة غير معرفة"));
            }
            if (fromUnit.Dimension != toUnit.Dimension)
            {
                return Result.Fail<UnitConversionGraph>(new(
                    "UNIT_DIMENSION_MISMATCH",
                    $"التحويل مرفوض بين وحدتين من بُعدين مختلفين ({fromUnit.Dimension}) <> ({toUnit.Dimension})"));
            }
            if (c.FromUomId == c.ToUomId)
            {
                return Result.Fail<UnitConversionGraph>(new(
                    "UNIT_CONVERSION_SELF",
                    "التحويل من الوحدة إلى نفسها مرفوض"));
            }
            if (c.Factor <= 0)
            {
                return Result.Fail<UnitConversionGraph>(new(
                    "UNIT_CONVERSION_FACTOR_INVALID",
                    "عامل التحويل يجب أن يكون موجباً"));
            }
        }

        var duplicate = conversions
            .GroupBy(c => (c.FromUomId, c.ToUomId))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return Result.Fail<UnitConversionGraph>(new(
                "UNIT_CONVERSION_DUPLICATE",
                $"يوجد أكثر من تحويل معرف للزوج {duplicate.Key.FromUomId}→{duplicate.Key.ToUomId}"));
        }

        var graph = new UnitConversionGraph(unitMap, conversions.ToList());

        // دورة موجهة؟ (أ→ب→ج→أ) — DFS بألوان؛ القاعدة: لا دورات إطلاقاً (§4.1)
        var color = unitMap.Keys.ToDictionary(id => id, _ => 0); // 0 أبيض · 1 رمادي · 2 أسود
        foreach (var start in unitMap.Keys)
        {
            if (color[start] != 0)
            {
                continue;
            }
            var cycle = FindCycle(start, graph._outgoing, color);
            if (cycle is not null)
            {
                return Result.Fail<UnitConversionGraph>(new(
                    "UNIT_CONVERSION_CYCLE",
                    "دورة تحويل مكتشفة: " + string.Join("→", cycle) + " — الدوائر ممنوعة"));
            }
        }

        // تعارض المسارات: BFS من كل وحدة؛ الوصول لنفس الوحدة بعاملين مختلفين = خلل تعريفي
        foreach (var start in unitMap.Keys)
        {
            var reach = new Dictionary<long, decimal> { [start] = 1m };
            var queue = new Queue<long>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var factorSoFar = reach[current];
                foreach (var edge in graph._outgoing.TryGetValue(current, out var outs) ? outs : [])
                {
                    var nextFactor = factorSoFar * edge.Factor;
                    if (reach.TryGetValue(edge.ToUomId, out var existing))
                    {
                        if (existing != nextFactor)
                        {
                            return Result.Fail<UnitConversionGraph>(new(
                                "UNIT_CONVERSION_AMBIGUOUS",
                                $"مساران بعاملين مختلفين إلى الوحدة {edge.ToUomId} — تعريف غير متسق"));
                        }
                        continue;
                    }
                    reach[edge.ToUomId] = nextFactor;
                    queue.Enqueue(edge.ToUomId);
                }
                foreach (var edge in graph._incoming.TryGetValue(current, out var ins) ? ins : [])
                {
                    var nextFactor = factorSoFar / edge.Factor;
                    if (reach.TryGetValue(edge.FromUomId, out var existing))
                    {
                        if (existing != nextFactor)
                        {
                            return Result.Fail<UnitConversionGraph>(new(
                                "UNIT_CONVERSION_AMBIGUOUS",
                                $"مساران بعاملين مختلفين إلى الوحدة {edge.FromUomId} — تعريف غير متسق"));
                        }
                        continue;
                    }
                    reach[edge.FromUomId] = nextFactor;
                    queue.Enqueue(edge.FromUomId);
                }
            }
        }

        return Result.Ok(graph);
    }

    private static List<long>? FindCycle(
        long start,
        Dictionary<long, List<ConversionDefinition>> outgoing,
        Dictionary<long, int> color)
    {
        color[start] = 1;
        foreach (var edge in outgoing.TryGetValue(start, out var outs) ? outs : [])
        {
            if (color[edge.ToUomId] == 1)
            {
                return [start, edge.ToUomId];
            }
            if (color[edge.ToUomId] == 0)
            {
                var deeper = FindCycle(edge.ToUomId, outgoing, color);
                if (deeper is not null)
                {
                    deeper.Insert(0, start);
                    return deeper;
                }
            }
        }
        color[start] = 2;
        return null;
    }

    public UnitDefinition Unit(long uomId) => _units[uomId];

    public bool HasUnit(long uomId) => _units.ContainsKey(uomId);

    /// <summary>عامل التحويل من وحدة إلى أخرى: الكمية بالمصدر × العامل = الكمية بالهدف.</summary>
    public Result<decimal> Factor(long fromUomId, long toUomId)
    {
        if (!_units.TryGetValue(fromUomId, out var fromUnit) || !_units.TryGetValue(toUomId, out var toUnit))
        {
            return Result.Fail<decimal>(new("UNIT_UNKNOWN", "وحدة قياس غير معرفة في المخطط"));
        }
        if (fromUomId == toUomId)
        {
            return Result.Ok(1m);
        }
        if (fromUnit.Dimension != toUnit.Dimension)
        {
            return Result.Fail<decimal>(new(
                "UNIT_DIMENSION_MISMATCH",
                $"التحويل مرفوض بين وحدتين من بُعدين مختلفين ({fromUnit.Dimension}) <> ({toUnit.Dimension})"));
        }

        // BFS عبر الحواف باتجاهين: مع الحافة × العامل، وعكسها ÷ العامل
        var reach = new Dictionary<long, decimal> { [fromUomId] = 1m };
        var queue = new Queue<long>();
        queue.Enqueue(fromUomId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var factorSoFar = reach[current];
            foreach (var edge in _outgoing.TryGetValue(current, out var outs) ? outs : [])
            {
                if (reach.ContainsKey(edge.ToUomId))
                {
                    continue;
                }
                var f = factorSoFar * edge.Factor;
                if (edge.ToUomId == toUomId)
                {
                    return Result.Ok(f);
                }
                reach[edge.ToUomId] = f;
                queue.Enqueue(edge.ToUomId);
            }
            foreach (var edge in _incoming.TryGetValue(current, out var ins) ? ins : [])
            {
                if (reach.ContainsKey(edge.FromUomId))
                {
                    continue;
                }
                var f = factorSoFar / edge.Factor;
                if (edge.FromUomId == toUomId)
                {
                    return Result.Ok(f);
                }
                reach[edge.FromUomId] = f;
                queue.Enqueue(edge.FromUomId);
            }
        }

        return Result.Fail<decimal>(new(
            "UNIT_CONVERSION_PATH_NOT_FOUND",
            $"لا يوجد تحويل معرف من الوحدة {fromUomId} إلى {toUomId}"));
    }
}
