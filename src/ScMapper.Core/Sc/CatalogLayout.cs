using System.Text.RegularExpressions;

namespace ScMapper.Core.Sc;

/// <summary>One entry of the binding tree: a category, a short label, and the actions (possibly from several game maps).</summary>
public sealed class UiGroup
{
    public string Category { get; init; } = "";
    public string Label { get; init; } = "";
    public string Key => Category + "/" + Label;
    public List<ActionDef> Actions { get; } = new();
}

/// <summary>
/// Star Citizen files its actions in ~50 "action maps" that follow the engine, not the player: one map mixes
/// fifteen topics (player: 144 actions), the same topic is split over several maps, and labels repeat their
/// category. This regroups every action by what it does, per action name, into a fixed category order with short,
/// unambiguous group names. The rules are first-match-wins per map; a known map without a matching rule falls back
/// to "Misc" of its category, an unknown map (new game patch) to "Other" with a readable name.
/// </summary>
public static class CatalogLayout
{
    private const string Flight = "Flight", Weapons = "Weapons & Targeting", Turrets = "Turrets", Systems = "Ship Systems",
        MiningSalvage = "Mining & Salvage", Ground = "Ground Vehicles", OnFoot = "On Foot", Tools = "Tools & Hacking",
        Interaction = "Interaction & Inventory", Eva = "E.V.A.", Social = "Social & Comms", Cameras = "Cameras & Views",
        Interface = "Interface & Menus", Developer = "Developer", Other = "Other";

    public static readonly string[] Categories =
        { Flight, Weapons, Turrets, Systems, MiningSalvage, Ground, OnFoot, Tools, Interaction, Eva, Social, Cameras,
          Interface, Developer, Other };

    private sealed record Rule(string Map, Regex Pattern, string Category, string Group);

    private static Rule R(string map, string pattern, string category, string group) =>
        new(map, new Regex("^(?:" + pattern + ")$", RegexOptions.Compiled | RegexOptions.CultureInvariant), category, group);

    // First match wins; the order of first appearance of a group is also its order inside the category.
    private static readonly Rule[] Rules =
    {
        R("spaceship_movement", @"v_(pitch|yaw|roll)(_.*)?|v_toggle_relative_mouse_mode|v_toggle_yaw_roll_swap|v_lock_rotation", Flight, "Rotation"),
        R("spaceship_movement", @"v_strafe_(up|down|vertical|left|right|lateral)|v_afterburner|v_space_brake", Flight, "Strafe, Boost & Brake"),
        R("spaceship_movement", @"v_strafe_(forward|back|longitudinal.*|trim_.*)|v_ifcs_throttle_.*", Flight, "Throttle & Trim"),
        R("spaceship_movement", @"v_ifcs_speed_limiter.*|v_accel_range.*", Flight, "Limiters"),
        R("spaceship_movement", @"v_ifcs_vector_decoupling.*|v_ifcs_gsafe.*|v_ifcs_toggle_gforce_safety|v_ifcs_toggle_esp|v_ifcs_esp_hold|v_master_mode.*", Flight, "Flight Modes"),
        R("spaceship_movement", @"v_ifcs_(gravity_compensation|wind_compensation|proximity_assist|stability|command|core)_.*|v_auto_precision_mode_.*", Flight, "IFCS Assists"),
        R("spaceship_movement", @"v_ifcs_reset_gmeter_max|v_flight_advanced_hud_.*", Systems, "HUD"),
        R("spaceship_movement", @"v_(toggle|deploy|retract)_landing_system|v_autoland|v_atc_.*", Flight, "Landing & Docking"),
        R("spaceship_movement", @"v_vtol_.*|v_transform_.*", Flight, "VTOL & Configuration"),
        R("spaceship_movement", @"v_toggle_jump_request", Flight, "Quantum & Jump"),
        R("spaceship_docking", @".*", Flight, "Landing & Docking"),
        R("spaceship_quantum", @".*", Flight, "Quantum & Jump"),
        R("IFCS_controls", @".*", Flight, "Flight Modes"),
        R("spaceship_view", @"v_view_(yaw|pitch|roll)(_.*)?", Cameras, "Ship Look"),
        R("spaceship_view", @"v_ads_.*", Weapons, "Targeting Aids"),
        R("spaceship_view", @".*", Cameras, "Ship Camera"),
        R("seat_general", @"v_view_look_behind", Cameras, "Ship Look"),
        R("spaceship_weapons", @"v_weapon_(gimbals|aim_type|staggered_fire|suppress_aim|convergence).*", Weapons, "Gimbals & Aiming"),
        R("spaceship_weapons", @"v_weapon_(pip|ui_scale).*", Weapons, "PIP & Gunnery HUD"),
        R("spaceship_weapons", @"v_weapon_preset.*", Weapons, "Weapon Presets"),
        R("spaceship_auto_weapons", @".*", Weapons, "Gimbals & Aiming"),
        R("spaceship_missiles", @".*", Weapons, "Missiles & Bombs"),
        R("spaceship_defensive", @".*", Weapons, "Countermeasures & Shields"),
        R("spaceship_targeting", @"v_(auto_targeting|look_ahead|target_tracking).*", Weapons, "Targeting Aids"),
        R("spaceship_targeting", @".*", Weapons, "Pinned Targets"),
        R("spaceship_targeting_advanced", @"v_target_cycle_(attacker|hostile|friendly)_.*", Weapons, "Cycle by Hostility"),
        R("spaceship_targeting_advanced", @".*", Weapons, "Cycle by Scope"),
        R("spaceship_target_hailing", @".*", Social, "Ship Comms"),
        R("turret_movement", @"turret_(pitch|yaw).*", Turrets, "Turret Aim"),
        R("turret_movement", @"turret_(toggle_mouse_mode|mouse_mode_.*)", Turrets, "Turret Mouse Modes"),
        R("turret_movement", @"turret_remote_.*", Turrets, "Remote Turrets"),
        R("turret_movement", @".*", Turrets, "Turret Assists"),
        R("turret_advanced", @"turret_change_position", Turrets, "Remote Turrets"),
        R("turret_advanced", @".*", Turrets, "Turret Assists"),
        R("seat_general", @"v_enter_remote_turret_\d", Turrets, "Remote Turrets"),
        R("seat_general", @"v_(emergency_exit|eject)", Systems, "Readiness & Emergency"),
        R("seat_general", @"v_light_amplification_.*", Systems, "Lights"),
        R("seat_general", @"v_(toggle|set)_.*_mode|v_operator_mode_.*", Systems, "Operator Modes & Seats"),
        R("spaceship_general", @"v_(self_destruct|flightready)", Systems, "Readiness & Emergency"),
        R("spaceship_general", @"v_cooler_throttle_.*", Systems, "Power & Cooling"),
        R("spaceship_general", @"v_.*(doors|ports|doorlocks|portlocks)", Systems, "Doors & Ports"),
        R("spaceship_general", @"pc_conversation_option\d", Social, "Conversation"),
        R("spaceship_general", @"spectate_.*", Cameras, "Spectator Camera"),
        R("spaceship_power", @"v_power_(throttle_.*)|v_engineering_assignment_.*", Systems, "Power Distribution"),
        R("spaceship_power", @"v_power_.*", Systems, "Power & Cooling"),
        R("lights_controller", @".*", Systems, "Lights"),
        R("spaceship_radar", @".*", Systems, "Scanning & Radar"),
        R("spaceship_scanning", @".*", Systems, "Scanning & Radar"),
        R("vehicle_mfd", @"v_mfd_(interact|movement)_.*", Systems, "MFD Navigation"),
        R("vehicle_mfd", @"v_mfd_soft_select_(mfd_primary|cast_).*", Systems, "MFD Select Primary & Casts"),
        R("vehicle_mfd", @"v_mfd_soft_select_mfd_\d+_.*", Systems, "MFD Select Screens 1-10"),
        R("vehicle_mfd", @"v_mfd_(select_view|quick_action)_.*", Systems, "MFD Page Select"),
        R("vehicle_mobiglas", @"ui_3d_display_.*", Systems, "3D Display"),
        R("spaceship_hud", @"v_comm_.*", Social, "Ship Comms"),
        R("spaceship_hud", @"mobiglas|toggle_ar_mode|v_hud_open_scoreboard|v_starmap|visor_wipe", Systems, "MobiGlas & Map"),
        R("spaceship_hud", @"v_.*", Systems, "HUD"),
        R("spaceship_mining", @".*", MiningSalvage, "Mining"),
        R("mining", @".*", MiningSalvage, "Mining"),
        R("spaceship_salvage", @"v_salvage_toggle_fire_.*|v_salvage_(toggle|reset)_gimbal.*|v_salvage_focus_.*", MiningSalvage, "Salvage Fire & Focus"),
        R("spaceship_salvage", @"v_salvage_nudge_.*", MiningSalvage, "Salvage Nudge"),
        R("spaceship_salvage", @"v_salvage_.*|tractor_beam_vehicle_.*", MiningSalvage, "Salvage Beam & Modifiers"),
        R("tractor_beam", @".*", Tools, "Hand Tractor Beam"),
        R("vehicle_general", @"v_view_.*", Ground, "Vehicle Camera"),
        R("vehicle_general", @"v_(toggle_all|open_all|close_all|lock_all|unlock_all)_.*", Ground, "Doors & Ports"),
        R("vehicle_general", @"v_horn", Ground, "Driving"),
        R("vehicle_general", @"mobiglas|v_flightready|v_starmap|visor_wipe|v_toggle_cursor_input", Ground, "Vehicle Interface"),
        R("vehicle_driver", @"v_view_.*", Ground, "Vehicle Camera"),
        R("vehicle_driver", @"v_.*", Ground, "Driving"),
        R("player", @"ui_3d_display_.*", OnFoot, "3D Display"),
        R("player", @"pc_conversation_.*|break_conversation_effects", Social, "Conversation"),
        R("player", @"hmd_.*", Interface, "VR & Head Tracking"),
        R("player", @"spectate_.*", Cameras, "Spectator Camera"),
        R("player", @"melee_AttackSyringeStab|combathealtarget|downedRevivalRequest|consume|dismiss_corpse_marker|force_respawn", OnFoot, "Medical & Respawn"),
        R("player", @"melee_.*|restrain|weapon_melee|takedown_.*|throw_.*", OnFoot, "Melee, Takedowns & Throws"),
        R("player", @"move(left|right|forward|back)|rotate(yaw|pitch)|gp_(movex|movey|rotateyaw|rotatepitch)|walk|sprint|fixed_speed_.*", OnFoot, "Movement"),
        R("player", @"jump.*|gp_jump|crouch|gp_crouch|prone|lean(left|right)|ledgegrab", OnFoot, "Stance & Jump"),
        R("player", @"select_.*_pit|selectUnarmedCombat|nextweapon|prevweapon|holster", OnFoot, "Weapon Selection"),
        R("player", @"attack1|attackSecondary|zoom|interact_with_scope|toggle_lowered|stabilize|reload.*|ammoRepool|weapon_.*|customize", OnFoot, "Firearms & Aiming"),
        R("player", @"toggle(Equip|Attach)Helmet|toggleHelmetState|visor_.*|toggle_flashlight", OnFoot, "Helmet, Visor & Light"),
        R("player", @"thirdperson|toggle_cursor_input|free_thirdperson_camera|pan_thirdperson_.*|zoom_in|zoom_out|shop_camera_.*", OnFoot, "Camera & Shop View"),
        R("player", @"mobiglas|toggle_ar_mode|ar_mode_scroll_.*|pl_hud_.*|v_starmap|ship_recall", OnFoot, "MobiGlas, Map & AR"),
        R("player", @"use|useAttachment.*|drop|inspect|selectitem|cancelselect|nextitem|prevItem|port_modification_select", Interaction, "Item Interaction"),
        R("prone", @".*", OnFoot, "Stance & Jump"),
        R("incapacitated", @".*", OnFoot, "Medical & Respawn"),
        R("player_choice", @"pc_pit_(inventory|looting.*|item_unstown|item_drop|empty_backpack)", Interaction, "Inventory & Looting"),
        R("player_choice", @"pc_pit_.*", Interaction, "PIT Categories"),
        R("player_choice", @"pc_qs_.*", Interaction, "Radial Quick Select"),
        R("player_choice", @"pc_.*", Interaction, "Interaction Controls"),
        R("hacking", @"hacking_minigame_debug_.*", Developer, "Debug"),
        R("hacking", @"hacking_minigame_(abort|help_window_toggle|ability_.*|cycle_input_mode)", Tools, "Hacking Abilities & Input"),
        R("hacking", @"hacking_minigame_.*", Tools, "Hacking Movement & Camera"),
        R("zero_gravity_eva", @"eva_(view|roll|toggle_headlook).*", Eva, "EVA Look & Roll"),
        R("zero_gravity_eva", @"eva_.*", Eva, "EVA Thrust"),
        R("zero_gravity_traversal", @"zgt_.*", Eva, "Surface Traversal"),
        R("player_emotes", @"emote_cs_.*|emote_launch", Social, "Hand Signals"),
        R("player_emotes", @"emote_.*", Social, "Emotes"),
        R("player_input_optical_tracking", @"foip_.*", Social, "Voice (VOIP/FOIP)"),
        R("player_input_optical_tracking", @"hmd_.*|headtrack_.*", Interface, "VR & Head Tracking"),
        R("ui_notification", @"ui_notification_.*", Social, "Chat & Invites"),
        R("default", @"notification_.*|toggle_contact|toggle_chat|cycle_chat_lobby|focus_on_chat_textinput", Social, "Chat & Invites"),
        R("default", @"respawn|retry|ready", OnFoot, "Medical & Respawn"),
        R("default", @"pl_exit", Systems, "Operator Modes & Seats"),
        R("default", @"objectives", Interface, "Map & Objectives"),
        R("default", @"cam_toggle_cinematic", Cameras, "Director Offsets & Lens"),
        R("default", @"flymode.*|toggle_trackview|toggle_action_profile", Developer, "Fly Mode & Tools"),
        R("default", @"ui_(copy|cut|paste)", Interface, "Text Editing"),
        R("default", @"flashui_.*", Interface, "On-Screen Keypad"),
        R("default", @"ui_(radialmenu_.*|primaryTab_.*|secondaryTab_.*|focus_.*|skip_video|hide_hint)|skip_cutscene", Interface, "Menu Tabs & Paging"),
        R("default", @"ui_.*", Interface, "Menu Navigation"),
        R("ui_textfield", @"ui_textfield_.*", Interface, "Text Editing"),
        R("mapui", @"mapui_(pan|cycle)_.*", Interface, "Map Navigation"),
        R("mapui", @"mapui_.*", Interface, "Map & Objectives"),
        R("stopwatch", @"stopwatch_.*", Interface, "Stopwatch"),
        R("character_customizer", @"character_customizer_(yaw|pitch|gp_yaw|gp_pitch|zoom_.*|enable_rotation|enable_mouse_rotation|toggle_face_tracking|yaw_.*|pitch_.*)", Interface, "Customizer View"),
        R("character_customizer", @"character_customizer_.*", Interface, "Customizer Editing"),
        R("view_director_mode", @"view_(save|load)_view_\d|view_reset_saved", Cameras, "Director Saved Views"),
        R("view_director_mode", @"view_.*", Cameras, "Director Offsets & Lens"),
        R("flycam", @"flycam_(move.*|speed.*|turbo)", Cameras, "Flycam Movement"),
        R("flycam", @"flycam_.*", Cameras, "Flycam Rotation & Path"),
        R("spectator", @"spectate_(next|prev|toggle_lock)_target|spectate_zoom.*|spectate_rotate.*|spectate_toggle_hud|spectate_gen_.*", Cameras, "Spectator Camera"),
        R("spectator", @"spectate_.*", Cameras, "Spectator Freecam"),
        R("debug", @".*", Developer, "Debug"),
        R("RemoteRigidEntityController", @"remote_.*", Developer, "Remote Entity Controller"),
        R("server_renderer", @".*", Developer, "Misc"),
    };

    private static readonly Dictionary<string, string> MapCategory = BuildMapCategory();

    private static Dictionary<string, string> BuildMapCategory()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rules) d.TryAdd(r.Map, r.Category);
        return d;
    }

    private static readonly Dictionary<(string Cat, string Group), int> GroupOrder = BuildGroupOrder();

    private static Dictionary<(string Cat, string Group), int> BuildGroupOrder()
    {
        var d = new Dictionary<(string Cat, string Group), int>();
        foreach (var r in Rules) d.TryAdd((r.Category, r.Group), d.Count);
        return d;
    }

    /// <summary>Where an action goes: category and group label.</summary>
    public static (string Category, string Group) Place(string map, string action)
    {
        foreach (var r in Rules)
            if (r.Map.Equals(map, StringComparison.OrdinalIgnoreCase) && r.Pattern.IsMatch(action))
                return (r.Category, r.Group);
        return MapCategory.TryGetValue(map, out var cat) ? (cat, "Misc") : (Other, Tidy(map));
    }

    /// <summary>Builds <see cref="ActionCatalog.Layout"/> and fixes wrong or technical action labels.</summary>
    public static void Apply(ActionCatalog catalog)
    {
        foreach (var a in catalog.AllActions) a.Label = FixLabel(a);

        var groups = new Dictionary<string, UiGroup>();
        foreach (var g in catalog.Groups)
            foreach (var a in g.Actions)
            {
                var (cat, label) = Place(a.Map, a.Name);
                var key = cat + "/" + label;
                if (!groups.TryGetValue(key, out var ug))
                    groups[key] = ug = new UiGroup { Category = cat, Label = label };
                ug.Actions.Add(a);
            }

        // Two actions of one group must never look the same (axis vs. mouse variants, ...).
        foreach (var ug in groups.Values)
            foreach (var dup in ug.Actions.GroupBy(a => a.Label, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
                foreach (var a in dup)
                {
                    var n = a.Name.ToLowerInvariant();
                    a.Label += n.Contains("mouse") ? " (Mouse)" : n.StartsWith("gp_") ? " (Gamepad)"
                        : dup.Any(x => x.Name.ToLowerInvariant().Contains("mouse")) ? " (Axis)" : $" ({a.Map})";
                }

        int Cat(string c) { int i = Array.IndexOf(Categories, c); return i < 0 ? Categories.Length : i; }
        catalog.Layout = groups.Values
            .OrderBy(g => Cat(g.Category))
            .ThenBy(g => GroupOrder.TryGetValue((g.Category, g.Label), out var o) ? o : int.MaxValue)
            .ThenBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ---- labels --------------------------------------------------------------------------------

    /// <summary>The game ships a few wrong labels (copy-paste); these replace them.</summary>
    private static readonly Dictionary<string, (string Wrong, string Right)> LabelFixes = new()
    {
        ["toggleHelmetState"] = ("Default Movement Speed Increase", "Helmet - Toggle State"),
        ["consume"] = ("Firearm - Attack", "Consume / Inject"),
        ["v_lights_off"] = ("Headlights (Enable)", "Headlights (Off)"),
        ["v_strafe_forward"] = ("Throttle - Increase", "Strafe Forward"),
    };

    private static readonly string[] TechPrefixes =
        { "V ", "Pc ", "Pl ", "Ui ", "Mapui ", "Flashui ", "Hacking Minigame ", "Spectate ", "Flycam ", "Remote ",
          "Character Customizer " };

    private static string FixLabel(ActionDef a)
    {
        // Some game labels carry a literal backslash-n or a real line break.
        var l = a.Label.Replace("\\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
        while (l.Contains("  ")) l = l.Replace("  ", " ");
        if (LabelFixes.TryGetValue(a.Name, out var fix) && l == fix.Wrong) return fix.Right;
        l = l.Replace("MDF ", "MFD ").Replace("Toogle", "Toggle");
        // Only labels the game never wrote (humanized action names) lose their technical prefix.
        var raw = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(a.Name.Replace('_', ' '));
        if (l == raw)
            foreach (var p in TechPrefixes)
                if (l.StartsWith(p, StringComparison.Ordinal) && l.Length > p.Length) { l = l[p.Length..]; break; }
        return l;
    }

    /// <summary>"some_new_map" / "SomeNewMap" -> "Some New Map".</summary>
    private static string Tidy(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '_') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(sb.ToString().ToLowerInvariant());
    }
}
