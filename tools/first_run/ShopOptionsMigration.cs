using System.Text.Json.Nodes;

internal static class ShopOptionsMigration
{
    private const string Exchange = "指定购买物品";
    private const string Guild = "指定协会商店购买物品";
    private const string Discount = "仅购买打折商品";

    private static readonly (string Group, string Source)[] Groups =
    {
        (Exchange, Exchange), ("交易所材料", Exchange),
        ("协会属性石", Guild), ("协会调教道具", Guild), (Guild, Guild)
    };

    private static JsonObject? Find(JsonArray options, string name) => options.OfType<JsonObject>()
        .FirstOrDefault(option => option["name"]?.ToString() == name);

    private static bool IsSet(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out bool enabled) && enabled;

    internal static void Apply(JsonObject instance, JsonArray tasks, JsonObject definition)
    {
        if (definition["option"] is not JsonObject definitions ||
            Groups.Any(group => definitions[group.Group]?["cases"] is not JsonArray)) return;

        // Work only from persisted rows. The frontend may already have filled its
        // in-memory rows with defaults by the time it loads a resource extension.
        bool wasGrouped = IsSet(instance["TkfmShopGroupsV2"]) || IsSet(instance["TkfmShopGroupsV3"]);
        bool hadMagicCrystal = IsSet(instance["TkfmShopCrystalDefaultsV1"]);
        foreach (var task in tasks.OfType<JsonObject>().Where(task => task["entry"]?.ToString() == "DailyShopStart"))
        {
            var options = task["option"] as JsonArray;
            var overrides = task["pipeline_override"] as JsonObject;
            bool migrated = false;
            if (options != null)
            {
                // Snapshot the two source lists before splitting either one. An
                // explicit [] is a user choice and is copied as an empty group.
                var exchange = Find(options, Exchange)?["selected_cases"] as JsonArray;
                var guild = Find(options, Guild)?["selected_cases"] as JsonArray;
                bool hasSeparateGroups = Groups.Any(group => group.Group != group.Source && Find(options, group.Group) != null);
                if (!wasGrouped && !hasSeparateGroups && (exchange != null || guild != null))
                {
                    foreach (var (name, source) in Groups)
                    {
                        var saved = source == Exchange ? exchange : guild;
                        if (saved == null) continue;
                        var selected = saved.Select(item => item?.ToString()).ToHashSet(StringComparer.Ordinal);
                        if (name == Guild && !hadMagicCrystal) selected.Add("魔晶石");
                        var cases = (JsonArray)definitions[name]!["cases"]!;
                        var matches = cases.OfType<JsonObject>()
                            .Select(item => item["name"]?.ToString())
                            .Where(caseName => caseName != null && selected.Contains(caseName));
                        var option = Find(options, name);
                        if (option == null)
                        {
                            option = new JsonObject { ["name"] = name, ["index"] = 0 };
                            options.Add(option);
                        }
                        option["selected_cases"] = new JsonArray(matches.Select(caseName => (JsonNode?)JsonValue.Create(caseName)).ToArray());
                    }
                    migrated = true;
                }
                for (int index = options.Count - 1; index >= 0; index--)
                {
                    if (options[index] is JsonObject option && option["name"]?.ToString() is string name
                        && (name == Exchange + Discount || name == Guild + Discount)
                        && !definitions.ContainsKey(name)) options.RemoveAt(index);
                }
            }
            overrides?.Remove("DailyShopExchangeSuppliesPolicy");
            overrides?.Remove("DailyShopGuildOtherPolicy");
            if (!migrated || overrides == null) continue;
            foreach (var (name, _) in Groups)
            foreach (var item in ((JsonArray)definitions[name]!["cases"]!).OfType<JsonObject>())
            {
                if (item["pipeline_override"] is not JsonObject defaults) continue;
                foreach (var target in defaults)
                {
                    if (overrides[target.Key] is not JsonObject cached) continue;
                    cached.Remove("enabled");
                    if (cached.Count == 0) overrides.Remove(target.Key);
                }
            }
        }
    }
}
