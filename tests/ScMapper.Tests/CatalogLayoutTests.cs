using System.Text.Json;
using ScMapper.Core.Sc;
using Xunit;

namespace ScMapper.Tests;

public class CatalogLayoutTests
{
    [Fact]
    public void Prone_action_and_its_rolls_share_one_group_and_no_group_is_called_prone()
    {
        var (c1, g1) = CatalogLayout.Place("player", "prone");
        var (c2, g2) = CatalogLayout.Place("prone", "prone_rollleft");
        Assert.Equal((c1, g1), (c2, g2));
        Assert.Equal("On Foot", c1);
        Assert.NotEqual("Prone", g1);
    }

    [Fact]
    public void Unknown_map_goes_to_other_with_readable_name_and_known_map_falls_back_to_misc()
    {
        Assert.Equal(("Other", "Some New Map"), CatalogLayout.Place("some_new_map", "x"));
        Assert.Equal(("On Foot", "Misc"), CatalogLayout.Place("player", "totally_new_action"));
    }

    [Fact]
    public void Apply_puts_every_action_in_exactly_one_group_and_fixes_known_wrong_labels()
    {
        var cat = new ActionCatalog();
        var g = new ActionGroup { Name = "player", Label = "x", Category = "x" };
        g.Actions.Add(new ActionDef { Map = "player", Name = "toggleHelmetState", Label = "Default Movement Speed Increase" });
        g.Actions.Add(new ActionDef { Map = "player", Name = "moveleft", Label = "Move Left" });
        g.Actions.Add(new ActionDef { Map = "player", Name = "rotateyaw", Label = "Look" });
        g.Actions.Add(new ActionDef { Map = "player", Name = "gp_rotateyaw", Label = "Look" });
        cat.Groups.Add(g);
        CatalogLayout.Apply(cat);

        Assert.Equal(4, cat.Layout.Sum(x => x.Actions.Count));
        Assert.Equal("Helmet - Toggle State", g.Actions[0].Label);
        Assert.NotEqual(g.Actions[2].Label, g.Actions[3].Label);
        CatalogLayout.Apply(cat);   // applying twice changes nothing
        Assert.Equal("Helmet - Toggle State", g.Actions[0].Label);
    }

    /// <summary>Runs against the real catalog cache when this machine has one; otherwise there is nothing to check.</summary>
    [Fact]
    public void Real_catalog_has_no_fallbacks_and_no_group_named_like_one_of_its_actions()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StarButton", "cache");
        var file = Directory.Exists(dir) ? Directory.GetFiles(dir, "catalog-v4-*.json").OrderBy(f => f).LastOrDefault() : null;
        if (file == null) return;
        var cat = JsonSerializer.Deserialize<ActionCatalog>(File.ReadAllText(file))!;
        CatalogLayout.Apply(cat);

        Assert.Equal(cat.AllActions.Count(), cat.Layout.Sum(g => g.Actions.Count));
        Assert.DoesNotContain(cat.Layout, g => g.Category == "Other");
        var clashes = new List<string>();
        foreach (var catGroup in cat.Layout.GroupBy(g => g.Category))
        {
            var labels = catGroup.SelectMany(g => g.Actions).Select(a => a.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
            clashes.AddRange(catGroup.Where(g => labels.Contains(g.Label)).Select(g => $"{g.Category}/{g.Label}"));
        }
        Assert.True(clashes.Count == 0, "group named like an action: " + string.Join(", ", clashes));
    }
}
