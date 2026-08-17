using System.Text.RegularExpressions;

namespace CustomsClearanceConsole;

internal sealed partial class DeclarationParser
{
    private static DeclarationRecord Reconcile(DeclarationRecord primary, DeclarationRecord secondary)
    {
        var conflicts = new List<string>();
        var recovered = new List<string>();
        var autoResolved = new List<string>();
        var agreements = 0;

        primary.DeclarationNo = SelectValue("报关单号", primary.DeclarationNo, secondary.DeclarationNo,
            x => x.Length == 18 && x.All(char.IsDigit), true, conflicts, recovered, autoResolved, ref agreements);
        primary.Consignee = SelectValue("境外收货人", primary.Consignee, secondary.Consignee,
            IsPlausibleText, false, conflicts, recovered, autoResolved, ref agreements);
        primary.ContractNo = SelectValue("合同协议号", primary.ContractNo, secondary.ContractNo,
            LooksLikeContract, false, conflicts, recovered, autoResolved, ref agreements);
        primary.ExitCustoms = SelectValue("出境关别", primary.ExitCustoms, secondary.ExitCustoms,
            IsPlausibleText, true, conflicts, recovered, autoResolved, ref agreements);
        primary.DestinationCountry = SelectValue("目的国", primary.DestinationCountry, secondary.DestinationCountry,
            IsPlausibleText, true, conflicts, recovered, autoResolved, ref agreements);

        primary.Totals = ReconcileTotals(primary.Totals, secondary.Totals, conflicts, recovered, ref agreements);
        var missing = MissingFields(primary);
        if (conflicts.Count > 0 || missing.Count > 0)
        {
            primary.Status = "需关注";
            var conflictWarning = conflicts.Count == 0 ? "" : $"双引擎结果不一致：{string.Join("；", conflicts)}";
            var missingWarning = missing.Count == 0 ? "" : $"未能可靠识别：{string.Join("、", missing)}";
            primary.Warning = JoinWarnings(conflictWarning, missingWarning);
            primary.Confidence = Math.Clamp((primary.Confidence + secondary.Confidence) / 2 - conflicts.Count * 7, 0, 84);
        }
        else
        {
            primary.Status = "双引擎校验通过";
            var notes = new List<string>();
            if (recovered.Count > 0) notes.Add($"第二引擎已补全：{string.Join("、", recovered.Distinct())}");
            if (autoResolved.Count > 0) notes.Add($"轻微字符差异已自动裁决：{string.Join("、", autoResolved.Distinct())}");
            primary.Warning = notes.Count == 0
                ? "Tesseract 与 PP-OCRv5 字段级复核一致"
                : string.Join("；", notes);
            primary.Confidence = Math.Clamp(Math.Max(primary.Confidence, secondary.Confidence) + Math.Min(8, agreements), 0, 99);
        }
        return primary;
    }

    private static Dictionary<string, decimal> ReconcileTotals(
        Dictionary<string, decimal> primary,
        Dictionary<string, decimal> secondary,
        List<string> conflicts,
        List<string> recovered,
        ref int agreements)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var currency in primary.Keys.Union(secondary.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var hasPrimary = primary.TryGetValue(currency, out var primaryAmount) && primaryAmount > 0;
            var hasSecondary = secondary.TryGetValue(currency, out var secondaryAmount) && secondaryAmount > 0;
            if (hasPrimary && hasSecondary && primaryAmount == secondaryAmount)
            {
                agreements++;
                totals[currency] = primaryAmount;
            }
            else if (hasPrimary && hasSecondary)
            {
                conflicts.Add($"关单总值 {currency}（主引擎 {primaryAmount:N2}；复核引擎 {secondaryAmount:N2}）");
                totals[currency] = secondaryAmount;
            }
            else if (hasPrimary)
            {
                totals[currency] = primaryAmount;
            }
            else if (hasSecondary)
            {
                totals[currency] = secondaryAmount;
                recovered.Add($"关单总值 {currency}");
            }
        }
        return totals;
    }

    private static string SelectValue(
        string field,
        string primary,
        string secondary,
        Func<string, bool> validator,
        bool preferSecondary,
        List<string> conflicts,
        List<string> recovered,
        List<string> autoResolved,
        ref int agreements)
    {
        primary = primary.Trim();
        secondary = secondary.Trim();
        var primaryValid = validator(primary);
        var secondaryValid = validator(secondary);
        if (primaryValid && secondaryValid && Equivalent(primary, secondary))
        {
            agreements++;
            return primary.Length >= secondary.Length ? primary : secondary;
        }
        if (primaryValid && secondaryValid && IsSafeNearMatch(field, primary, secondary))
        {
            agreements++;
            autoResolved.Add(field);
            if (field == "合同协议号")
                return NormalizeComparable(primary).Length >= NormalizeComparable(secondary).Length ? primary : secondary;
            return primary;
        }
        if (primaryValid && !secondaryValid) return primary;
        if (!primaryValid && secondaryValid)
        {
            recovered.Add(field);
            return secondary;
        }
        if (!primaryValid && !secondaryValid) return primary.Length >= secondary.Length ? primary : secondary;

        conflicts.Add($"{field}（主引擎“{CompactForWarning(primary)}”；复核引擎“{CompactForWarning(secondary)}”）");
        return preferSecondary ? secondary : primary;
    }

    private static bool Equivalent(string first, string second)
    {
        return NormalizeComparable(first) == NormalizeComparable(second);
    }

    private static string NormalizeComparable(string value) =>
        Regex.Replace(value.ToUpperInvariant(), @"[^0-9A-Z\u4e00-\u9fff]", "");

    private static bool IsSafeNearMatch(string field, string first, string second)
    {
        var a = NormalizeComparable(first);
        var b = NormalizeComparable(second);
        if (field == "境外收货人" && a.Length >= 12 && b.Length >= 12 &&
            a.All(x => x is >= 'A' and <= 'Z' or >= '0' and <= '9') &&
            b.All(x => x is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            return LevenshteinDistanceAtMostOne(a, b);

        if (field != "合同协议号" || Math.Abs(a.Length - b.Length) != 1) return false;
        var longer = a.Length > b.Length ? a : b;
        var shorter = a.Length > b.Length ? b : a;
        for (var i = 0; i < longer.Length; i++)
        {
            if (!char.IsLetter(longer[i])) continue;
            if (longer.Remove(i, 1).Equals(shorter, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool LevenshteinDistanceAtMostOne(string first, string second)
    {
        if (Math.Abs(first.Length - second.Length) > 1) return false;
        if (first.Length > second.Length) (first, second) = (second, first);
        var edits = 0;
        for (int i = 0, j = 0; i < first.Length || j < second.Length;)
        {
            if (i < first.Length && j < second.Length && first[i] == second[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (first.Length == second.Length) { i++; j++; }
            else j++;
        }
        return true;
    }

    private static bool IsPlausibleText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2) return false;
        var useful = value.Count(x => char.IsLetterOrDigit(x) || x is >= '\u4e00' and <= '\u9fff');
        return useful >= Math.Max(2, value.Length / 2);
    }

    private static string CompactForWarning(string value) => value.Length <= 42 ? value : value[..39] + "…";

    private static List<string> MissingFields(DeclarationRecord record)
    {
        var missing = new List<string>();
        if (record.DeclarationNo.Length != 18) missing.Add("报关单号");
        if (string.IsNullOrWhiteSpace(record.Consignee)) missing.Add("境外收货人");
        if (string.IsNullOrWhiteSpace(record.ContractNo)) missing.Add("合同协议号");
        if (string.IsNullOrWhiteSpace(record.ExitCustoms)) missing.Add("出境关别");
        if (string.IsNullOrWhiteSpace(record.DestinationCountry)) missing.Add("目的国");
        if (record.Totals.Count == 0) missing.Add("关单总货值");
        return missing;
    }

    private static string JoinWarnings(params string[] warnings) =>
        string.Join("；", warnings.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().TrimEnd('；')).Distinct());
}
