using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Controller Support Plus: controller-first inventory flow for Valheim.
// 100% client-side. Every action here is something vanilla already allows with more button presses.
// Built with the C# 5 compiler that ships with Windows, so no newer language features are used.
namespace ControllerPro
{
    [BepInPlugin(Guid, "Controller Support Plus", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "rivolus.controllersupportplus";
        public const string Version = "1.0.1";
        /* Jotunn's Harmony id: the mod's ZInput postfixes run after its own (1.0.1, a player's report: with a Jotunn mod that adds
           key binds, LT + D-pad down also sat and LT + A also jumped) */
        public const string JotunnGuid = "com.jotunn.jotunn";

        public static ConfigEntry<bool> Enabled, ReplaceVanillaTriggers, ShowHints, HideVanillaHints;
        public static ConfigEntry<float> HoldShort, HoldLong, RingDelay;
        public static ConfigEntry<float> SliderStartSpeed, SliderMaxSpeed, HintOffsetY, HintScale;
        public static ConfigEntry<int> DpadStep;
        public static BepInEx.Logging.ManualLogSource Log;

        // Section names. The in-game settings menu (F1) lists sections in the order they are declared below (unless its own
        // "Sort by name" is on), so they are declared by priority: the Controller Hotbar and the HUD style first, then their
        // detailed settings, the rest after, the developer tools last. (A mod manager may still list them alphabetically.)
        const string SecHotbar = "Controller Hotbar", SecStyle = "HUD Style", SecLayout = "HUD Style - Settings", SecHold = "Button Hold",
            SecCursor = "Controller Cursor", SecInvGuide = "Inventory Button Guide", SecPlayGuide = "Gameplay Button Guide";

        // A setting moved to a new section or key: its saved value is read from the old place, carried over (optionally
        // translated), and the old entry removed from the settings file.
        void Carry<T>(string sec, string key, ConfigEntry<T> to, Func<string, string> map = null)
        {
            ConfigEntry<string> old = Config.Bind(sec, key, "", "old");
            string v = old.Value != null ? old.Value.Trim() : "";
            Config.Remove(old.Definition);
            if (v.Length == 0) return;
            if (map != null) v = map(v);
            try { to.Value = (T)TomlTypeConverter.ConvertToValue(v, typeof(T)); } catch (Exception) { }
        }

        // dev tool (0.91.1, Rob): every setting back to its default, in the mod manager's file and in internal-settings.cfg,
        // as on a first install (the HUD pick's plaque comes back). The developer tools' own settings (captures, export) stay.
        static Plugin self;
#if DEV
        /* developer build only (V1 clean-up): it names the developer tools' settings, which the shareable DLL must not hold */
        public static void ResetAll()
        {
            int n = 0;
            foreach (ConfigFile f in new ConfigFile[] { self != null ? self.Config : null, Internal })
            {
                if (f == null) continue;
                List<ConfigEntryBase> list = new List<ConfigEntryBase>();
                foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> kv in f) list.Add(kv.Value);
                // the preset first: its own change handling resets the styles, then everything else on top
                list.Sort(delegate(ConfigEntryBase a, ConfigEntryBase b) { return (a == HudPreset ? 0 : 1).CompareTo(b == HudPreset ? 0 : 1); });
                foreach (ConfigEntryBase e in list)
                {
                    string sec = e.Definition.Section;
                    /* the screen measurements (Text styles) stay too (0.91.14: resetting them dropped Body text to its small first guess until the pause menu was opened again) */
                    if (sec == "Design captures" || sec == "Design export" || sec == "Text styles") continue;
                    if (!Equals(e.BoxedValue, e.DefaultValue)) { e.BoxedValue = e.DefaultValue; n++; }
                }
                f.Save();
            }
            Log.LogInfo("Dev reset: " + n + " settings put back to their defaults");
        }
#endif

        private void Awake()
        {
            self = this;
            Log = Logger;
            // Every setting is declared first, in the menu's order; only then are older settings read and cleared out (clearing
            // them in between would let later settings take their places in the list and shuffle the order).
            // ----- the settings in the mod manager: exactly the ones in the Controller Support Plus Settings window (0.89.0) -----
            /* "1. General", as the window's General tab (1.0.1; "1. HUD layout" before, carried over below) */
            HudPreset = Config.Bind("1. General", "HUDPreset", "Vanilla", new ConfigDescription("Vanilla (the game's HUD as it is), BottomHUD (resource bars bottom left, hotbar bottom centre, minimap bottom right) or ActionRPG (resource bars top left, hotbar bottom left, minimap top right). Each preset sets its own good starting choices for the styles below.", new AcceptableValueList<string>("Vanilla", "BottomHUD", "ActionRPG")));
            /* 1.0.1 (Rob): the game's own auto-pickup as a setting; on the Controller Support Plus layout its block + left stick chord is off */
            AutoPickup = Config.Bind("4. Misc", "AutoPickup", true, "The game's own auto-pickup: items you walk over go straight into the bag. The same switch as the game's own toggle key.");
            /* 1.0.1 (Rob): the controller cursor in the bag, the build menu and the pickers (an internal setting before, carried over below) */
            CursorEnabled = Config.Bind("1. General", "ControllerCursor", true, "Using the left stick inside the inventory, the build menu and the pickers brings up a controller cursor. Using the D-pad puts it away again.");
            HotbarStyle = Config.Bind("2. Hotbar", "HotbarLayout", "Cross", new ConfigDescription("How the Controller Hotbar looks: Cross, CompactCross or Row.", new AcceptableValueList<string>("Cross", "CompactCross", "Row")));
            SwapDpadActions = Config.Bind("2. Hotbar", "SwapDpadItemsAndLTActions", false, "Off: the D-pad uses hotbar items 1-4 and LT + D-pad does the actions (consumables, emotes, hammer, sit). On: the other way round.");
            /* 1.0.1 (Rob): the LT + D-pad layer itself on or off; off, LT leaves the D-pad as it is */
            LtDpadLayer = Config.Bind("2. Hotbar", "LTDpadLayer", true, "On: holding LT switches the D-pad to its other set (the actions, or the items when swapped). Off: LT leaves the D-pad as it is.");
            ShowMapHints = Config.Bind("3. HUD elements", "ShowButtonHints", true, "Show the game's button hints (Attack, Block, Dodge, the build hints, the radial wheel's hints) on the controller.");
            /* Compact renamed Detailed (0.92.13): an old Compact in the file is read before it is thrown out as unknown */
            bool wasCompact = false;
            try { wasCompact = System.IO.File.Exists(Config.ConfigFilePath) && System.Text.RegularExpressions.Regex.IsMatch(System.IO.File.ReadAllText(Config.ConfigFilePath), @"BuffsAndDebuffsStyle\s*=\s*Compact"); } catch (Exception) { }
            BuffStyle = Config.Bind("3. HUD elements", "BuffsAndDebuffsStyle", "Slim", new ConfigDescription("Bottom HUD and Action RPG: Vanilla (the game's own), Detailed (small boxes with a green frame for buffs, red for debuffs, grey for neutral ones) or Slim (floating icons, a red arrow on debuffs).", new AcceptableValueList<string>("Vanilla", "Detailed", "Slim")));
            if (wasCompact) BuffStyle.Value = "Detailed";
            FeedPos = Config.Bind("3. HUD elements", "PickUpNotificationStyle", "Vanilla", new ConfigDescription("New (Modern: the mod's pickup list at the right edge) or Vanilla (the game's own message in the top left).", new AcceptableValueList<string>("New", "Vanilla")));
            EquipProgress = Config.Bind("3. HUD elements", "EquipProgressBarStyle", "Vanilla", new ConfigDescription("Ring (Radial progress: a ring around the crosshair) or Vanilla (the game's own bar).", new AcceptableValueList<string>("Ring", "Vanilla")));
            LtSelectSettings = Config.Bind("1. General", "LTSelectOpensSettings", false, "LT + Select opens the Controller Support Plus Settings straight from play, instead of the chat.");
            HudOnKbm = Config.Bind("4. Misc", "KeepHUDLayoutOnKeyboardAndMouse", false, "Keep the HUD preset's layout while using keyboard and mouse.");
            // their values from before 0.89.0, carried over from their old places
            Carry("HUD Style", "Preset", HudPreset);
            Carry("1. HUD layout", "HUDPreset", HudPreset);   /* its section before 1.0.1, read last: the newest wins */
            Carry("Controller Hotbar", "HotbarStyle", HotbarStyle);
            Carry("HUD Style - Settings", "ShowButtonGuide", ShowMapHints);
            Carry("HUD Style - Settings", "BuffsAndDebuffs", BuffStyle);
            Carry("HUD Style - Settings", "PickUpNotificationStyle", FeedPos);
            Carry("Crosshair", "EquipProgressBar", EquipProgress);
            Carry("HUD Style - Settings", "LTSelectOpensSettings", LtSelectSettings);
            Carry("Controller Hotbar", "SwapDpadActions", SwapDpadActions);
            Carry("HUD Style - Settings", "ApplyHudPresetToKeyboardAndMouse", HudOnKbm);
            // and from their places in 0.89.0-0.90.3, before the Hotbar group (0.90.4)
            Carry("2. HUD elements", "HotbarLayout", HotbarStyle);
            Carry("3. Input and compatibility", "SwapDpadItemsAndLTActions", SwapDpadActions);
            Carry("2. HUD elements", "ButtonHints", ShowMapHints);
            ShowMapHints.SettingChanged += delegate { HintsLink.FromMod(); };   /* one setting with the game's own Show button hints (1.0.0) */
            Carry("2. HUD elements", "BuffsAndDebuffsStyle", BuffStyle);
            Carry("2. HUD elements", "PickUpNotificationStyle", FeedPos);
            Carry("2. HUD elements", "EquipProgressBarStyle", EquipProgress);
            Carry("3. Input and compatibility", "LTSelectOpensSettings", LtSelectSettings);
            Carry("3. Input and compatibility", "KeepHUDLayoutOnKeyboardAndMouse", HudOnKbm);
            /* 1.0.4 (Rob): "Input and compatibility" became "Misc"; LT + Select moved to General, Auto-pickup to Misc */
            Carry("4. Input and compatibility", "LTSelectOpensSettings", LtSelectSettings);
            Carry("4. Input and compatibility", "KeepHUDLayoutOnKeyboardAndMouse", HudOnKbm);
            Carry("1. General", "AutoPickup", AutoPickup);
            if (HudPreset.Value != "Vanilla" && HudPreset.Value != "BottomHUD" && HudPreset.Value != "ActionRPG") HudPreset.Value = "Vanilla";

            // ----- everything else: fixed at the mod's own choices, in a file of the mod's own beside it, not in the mod manager -----
            // A few are still remembered there: whether PRO is on (picked in the game's own controller settings), what the preset
            // picked for the minimap and the bars, and what the mod measured. The rest just keep their built-in values.
            Internal = new ConfigFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Info.Location), "internal-settings.cfg"), true, Info.Metadata);
            FavEnabled = Internal.Bind(SecHotbar, "Enabled", true, "D-pad favorites. While on, the D-pad no longer cycles the hotbar.");
            FavSecondary = Internal.Bind(SecHotbar, "SecondarySet", true, "A second set of four favorites on the face buttons while LT is held. While it is on, LT no longer opens the radial menu or stows the weapon. Only works in the Alternative 1 controller layout, where LT is not block.");
            HideHotbar = Internal.Bind(SecHotbar, "HideVanillaHotbar", true, "Hide Valheim's own hotbar (the row of 8 at the top left) while the Controller Hotbar is in use. Not the Controller Hotbar itself. The same items are still the first row of the inventory.");
            FavMarkers = Internal.Bind(SecHotbar, "MarkItemsInBag", true, "On the controller, show the Controller Hotbar button an item is on in its bag slot's top left corner, in place of the game's number keys (which are hidden on the controller).");
            FavScale = Internal.Bind(SecHotbar, "ClusterScale", 1f, "Size of the favorites cluster.");
            FavDownHold = Internal.Bind(SecHotbar, "DownHoldSeconds", 0.40f, "With a favorite on D-pad down: tap uses the favorite, holding this long does the vanilla D-pad down action instead.");
            FavFlash = Internal.Bind(SecHotbar, "EquippedFlashSeconds", 0.3f, "Length of the white flash on a favorite the moment its item finishes equipping. 0 turns it off.");
            // archived for a possible future setting (Rob, 0.90.0): LT in the middle of the face-button set, as before 0.90.0
            LtInSetMiddle = Internal.Bind(SecHotbar, "LTInFaceSetMiddle", false, "Cross and compact cross only. Off (since 0.90.0): LT sits at the bottom between the two sets, as in the row, with the face glyphs at 30% until it is held. On (the archived look): LT in the middle of the face-button set, fading out while held, the face glyphs only while it is held.");
            FavAssignSound = Internal.Bind(SecHotbar, "AssignSound", true, "Play the game's equip sound when an item is put on a favorite in the assign overlay.");
            FavDimSprint = Internal.Bind(SecHotbar, "DimWhileSprinting", true, "Favorites do not respond while sprinting; pressing one flashes it red. (The name is from when the cluster also dimmed. It no longer does.)");
            ResourceStyle = Internal.Bind(SecLayout, "ResourceBarStyle", "Vanilla", new ConfigDescription("Your preferred resource bars: Vanilla (the game's own), Horizontal (the mod's horizontal bars for health, stamina and eitr, grouped with the food and the Forsaken power) or Mixed (the same group, but stamina and eitr stay the game's own bars, in their place). Used where the preset supports it: Bottom HUD supports all three; Action RPG supports Horizontal and Mixed, and uses Mixed when this is Vanilla (the game's own health and food cannot go to the top left); the Vanilla preset always uses the game's own.", new AcceptableValueList<string>("Vanilla", "Horizontal", "Mixed")));
            BottomBarSide = Internal.Bind(SecLayout, "BottomHUDPosition", "Center", new ConfigDescription("Bottom HUD only: where the Controller Hotbar (the action bar) sits, Center (bottom centre, with the game's stamina bar lifted above it) or Right (bottom right, just left of the minimap). The other presets ignore it.", new AcceptableValueList<string>("Center", "Right")));
            RpgMap = Internal.Bind(SecLayout, "ActionRPGMinimapPosition", "Bottom", new ConfigDescription("Action RPG only: the minimap at the Top right (its gameplay hints under it) or the Bottom right (the hints above it, reading upward). The other presets ignore it.", new AcceptableValueList<string>("Top", "Bottom")));
            WheelHintsExtra = Internal.Bind("Gameplay Button Guide", "WheelHintsExtraDistance", 0f, "How much further out from the radial wheel's centre its button guide sits, on top of the wheel's own size. The same for every wheel.");
            /* the game's own text sizes, measured, kept in the HUD's units so they follow the resolution (0.92.30, Rob: kept in
               screen pixels before, they stayed 1440p-sized at 720p and the text came out huge); the old pixel entries are cleared */
            HeaderSeen = Internal.Bind("Text styles", "HeaderSize", 0f, "Set by the mod: how tall the game's own Settings screen labels are, in HUD units (so it follows the resolution), measured when that screen is open. The Header text style is drawn at this size. 0 to measure again.");
            BodySeen = Internal.Bind("Text styles", "BodySize", 0f, "Set by the mod: how tall the labels on the pause menu's controller picture are, in HUD units (so it follows the resolution), measured while that picture shows. The Body text style is drawn at this size. 0 to measure again.");
            foreach (string oldKey in new[] { "HeaderScreenSize", "BodyScreenSize" }) { ConfigEntry<float> oldPx = Internal.Bind("Text styles", oldKey, 0f, "old"); Internal.Remove(oldPx.Definition); }
            WheelReachSeen = Internal.Bind("Gameplay Button Guide", "WheelReachSeen", 0f, "Set by the mod: the widest a radial wheel has been drawn, from its centre. The wheel's button guide sits beyond it. 0 to measure again.");
            HudPickSeen = Internal.Bind(SecLayout, "HudLayoutPickDone", false, "Set by the mod: true once a HUD layout has been confirmed in the first-launch pick, so its plaque does not show again. false shows the plaque again. The pick can always be opened from Controller Support Plus Settings.");
            ShowMinimap = Internal.Bind(SecLayout, "ShowMinimap", true, "Show the small minimap on the controller, in every preset. Off hides it; its gameplay hints move up into its place and the status effects into the corner.");
            BuffPos = Internal.Bind(SecLayout, "BuffsAndDebuffsPosition", "TopRight (Vanilla)", new ConfigDescription("Where the slim buffs and debuffs sit: TopRight (Vanilla) (where the game has them), TopLeft (in the resource bars group, between the bars and the Forsaken power; needs Horizontal or Mixed bars, and in Bottom HUD that group is in the bottom left), BottomRight (just left of the minimap, lined up with its bottom; beside it at the top when the minimap is at the top), BottomLeft (a column in the bottom left corner reading upward, above the hotbar or the resource bars when they are there) or BottomCenter (a centred row at the bottom, above the hotbar and the game's stamina bar when they are there). A place the layout cannot give falls back to TopRight.", new AcceptableValueList<string>("TopRight (Vanilla)", "TopLeft", "BottomRight", "BottomLeft", "BottomCenter")));
            ProLayout = Internal.Bind("Controls", "UseProLayout", true, "Use the Controller Support Plus controller layout (called PRO before 1.0.1): the game's Alternative 1 layout with the mod's remaps on top. It is also a fourth choice in the game's own controller layout setting, which is the easier place to change it. Picking Classic, Alternative 1 or Alternative 2 there turns this off.");
            RemapDodge = Internal.Bind("Controls", "DodgeAndSneakOnB", true, "Controller Support Plus layout only: tapping B dodges on its own (no need to hold block), and holding B toggles sneak.");
            SheatheOnRs = Internal.Bind("Controls", "SheatheOnRightStick", true, "Controller Support Plus layout only: clicking the right stick puts away the weapon or tool in hand, or takes it back out (the game's own sheathe), instead of sneaking. Only while DodgeAndSneakOnB is on, so sneak stays on holding B. With the hammer out the click stays the game's own.");
            ObjectSlots = Internal.Bind("Controls", "ObjectSlots", true, "On the controller, using a mannequin, an item stand, an item frame or a boss altar opens the bag with the object's slots under it, like a chest. RT puts the focused item on it at once, RT on its slots takes it back. Items that cannot go on it are greyed. Every rule is the object's own.");
            SneakHold = Internal.Bind("Controls", "SneakHoldSeconds", 0.35f, "How long B is held before it toggles sneak instead of dodging.");
            PowerChord = Internal.Bind("Controls", "ForsakenPowerOnLBAndRB", true, "Controller Support Plus layout only: pressing LB and RB together uses the Forsaken power.");
            ChordWindow = Internal.Bind("Controls", "TogetherWindowSeconds", 0.12f, "How close together LB and RB must be pressed to count as pressed together. Holding one and pressing the other later is not the combination.");
            ChordGrace = Internal.Bind("Controls", "RBWaitSeconds", 0.05f, "While a Forsaken power is ready, RB waits this long before doing its own job, to see whether LB joins it. Without it, pressing RB a hair before LB starts an attack. 0 turns the wait off.");
            Enabled = Internal.Bind("General", "Enabled", true, "Master switch. The mod only acts while a controller is the active input; it steps aside as soon as keyboard or mouse is used.");
            ReplaceVanillaTriggers = Internal.Bind("General", "ReplaceVanillaTriggerCombos", true, "Turns off vanilla LT+A (split), RT+A (drop) and LT+X (move) inside the inventory, so LT can be the modifier layer.");
            DotCrosshair = Internal.Bind("Crosshair", "DotCrosshair", true, "On the controller, show a small dot in the middle of the screen instead of the game's cross. With keyboard or mouse the cross comes back.");
            EquipRingFor = Internal.Bind("Crosshair", "EquipRingShowsFor", "Everything", new ConfigDescription("With the Ring: Everything (every equip, unequip or reload that takes any time) or VanillaBarsOnly (only the longer ones the game shows its own bar for, over half a second).", new AcceptableValueList<string>("Everything", "VanillaBarsOnly")));
            EquipRingLabel = Internal.Bind("Crosshair", "EquipRingLabel", true, "With the Ring: show the action's name (Equipping ...) to the right of the ring, for the actions the game shows its own bar for. It fades out while the game shows a prompt by the crosshair.");
            /* the cursor's on/off moved to the mod manager's file (1.0.1): an Off kept here before is carried over, then cleared */
            ConfigEntry<bool> oldCursor = Internal.Bind(SecCursor, "Enabled", true, "old");
            if (!oldCursor.Value) CursorEnabled.Value = false;
            Internal.Remove(oldCursor.Definition);
            CursorSlotSpeed = Internal.Bind(SecCursor, "SpeedOverSlotsAndButtons", 720f, "Cursor speed while it is over something that can be pressed. Pixels per second on a 1080-high screen, at full stick.");
            CursorPanelSpeed = Internal.Bind(SecCursor, "SpeedInsidePanels", 1250f, "Cursor speed while it is over a panel but not over a slot or button.");
            ScrollSpeed = Internal.Bind(SecCursor, "ListScrollSpeed", 1200f, "How fast the right stick scrolls a list in the inventory (the recipe list, a large chest), at full stick. 0 turns it off.");
            CursorTravelSpeed = Internal.Bind(SecCursor, "SpeedBetweenPanels", 3000f, "Top cursor speed in the open space between panels. It builds up to this in a fraction of a second.");
            ShowHints = Internal.Bind(SecInvGuide, "ShowHintStrip", true, "Show the controller hint strip under the inventory.");
            HideVanillaHints = Internal.Bind(SecInvGuide, "HideVanillaInventoryHints", true, "Hide the vanilla inventory hints at the bottom of the screen while the strip is showing.");
            HintOffsetY = Internal.Bind(SecInvGuide, "GapToButtons", 16f, "Space between the bottom of the inventory panel's art and the top of the buttons in the guide.");
            HintPadding = Internal.Bind(SecInvGuide, "PaddingTopBottom", 16f, "Space above and below the buttons inside the strip.");
            HintScale = Internal.Bind(SecInvGuide, "StripScale", 1f, "Size of the strip.");
            StripExtraWidth = Internal.Bind(SecInvGuide, "ExtraWidth", 80f, "How much wider than the inventory panel the button guide may run, to the right, so longer labels fit.");
            StripBackground = Internal.Bind(SecInvGuide, "StripBackground", false, "Draw the wood panel behind the button guide. Off leaves the guide floating below the inventory. Takes effect the next time the game starts.");
            GlyphSize = Internal.Bind(SecInvGuide, "ButtonGlyphSize", 16f, "Size of the button glyphs in the strip. They are the game's own glyphs. Kept small so the hold ring around them stays clearly visible.");
            GamePanelStyle = Internal.Bind(SecInvGuide, "UseGamePanelStyle", true, "Give the hint strip the same background art as the inventory panel. Off uses a flat dark bar. Takes effect the next time the game starts.");
            HideHud = Internal.Bind(SecInvGuide, "HideHudBehindInventory", true, "On the controller, fade out the rest of the HUD (minimap, health, food, Forsaken power, status effects) while the inventory is open.");
            StackHudHints = Internal.Bind(SecPlayGuide, "StackUnderMinimap", true, "On the controller, stack the game's own gameplay hints (Attack, Block, Dodge...) vertically under the minimap, clear of the favorites.");
            HudHintsGap = Internal.Bind(SecPlayGuide, "GapBelowMinimap", 20f, "Space between the minimap and the first hint.");
            HudHintsSpacing = Internal.Bind(SecPlayGuide, "SpaceBetweenStackedHints", 12f, "Clear space between rows in every stacked button guide: the gameplay hints under the minimap and the hints beside the crafting panel. Both use this one value, so they always match.");
            DimPanels = Internal.Bind("Panels", "DimInactivePanels", true, "On the controller, darken the inventory panels that are not the one in use, so the active panel stands out.");
            DimStrength = Internal.Bind("Panels", "DimStrength", 0.5f, "How dark an inactive panel gets. 0 is no change, 1 is black.");
            HoldShort = Internal.Bind(SecHold, "ShortHoldSeconds", 0.40f, "Hold length for convenience actions (hold Y to split).");
            HoldLong = Internal.Bind(SecHold, "LongHoldSeconds", 0.60f, "Hold length for actions that lose items (hold B to drop).");
            RingDelay = Internal.Bind(SecHold, "RingDelaySeconds", 0.18f, "Quiet period before the hold ring shows, so a plain tap never flashes it.");
            RingFullBeat = Internal.Bind(SecHold, "FullRingBeatSeconds", 0.08f, "How long the completed ring stays on screen before the hold action fires, so the fill is seen to finish.");
            SliderStartSpeed = Internal.Bind("Split slider", "StartSpeed", 6f, "Items per second when the stick has just started being held.");
            SliderMaxSpeed = Internal.Bind("Split slider", "MaxSpeed", 60f, "Items per second once the stick has been held for about a second.");
            DpadStep = Internal.Bind("Split slider", "DpadStep", 5, "Amount D-pad up / down changes the split by.");
#if DEV
            Captures = Internal.Bind("Design captures", "Enabled", false, "Save a screenshot of each mod UI state when it appears, for reviewing the design outside the game. Turn off for normal play.");
#endif
#if DEV
            CaptureFolder = Internal.Bind("Design captures", "Folder", System.IO.Path.Combine(Paths.BepInExRootPath, "captures"), "Where the screenshots are saved. One file per state, replaced each time.");
#endif
#if DEV
            ExportKit = Internal.Bind("Design export", "Enabled", false, "Once per game session, when the inventory is first opened, save the game's UI art, controller glyphs and a layout spec for use in a design tool. Causes a short pause. Turn off for normal play.");
#endif
#if DEV
            ExportFolder = Internal.Bind("Design export", "Folder", System.IO.Path.Combine(Paths.BepInExRootPath, "design-export"), "Where the exported art and spec.json are saved.");
#endif
            // carried over into the internal file: the PRO choice, the developer captures, and the wheel the mod measured
            Carry("Controls", "UseProLayout", ProLayout);
#if DEV
            Carry("Design captures", "Enabled", Captures); Carry("Design captures", "Folder", CaptureFolder);
            Carry("Design export", "Enabled", ExportKit); Carry("Design export", "Folder", ExportFolder);
#endif
            Carry("Gameplay Button Guide", "WheelReachSeen", WheelReachSeen);
            Internal.Save();
            // every leftover entry of earlier versions out of the settings file, so the mod manager shows only the nine above
            try
            {
                PropertyInfo orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                System.Collections.IDictionary od = orphans != null ? orphans.GetValue(Config, null) as System.Collections.IDictionary : null;
                if (od != null && od.Count > 0) { Log.LogInfo("Settings: " + od.Count + " old entries cleared out of the settings file"); od.Clear(); }
            }
            catch (Exception e) { Log.LogWarning("Settings: old entries not cleared: " + e.Message); }
            // A new preset starts from its defaults: every HUD style setting goes back to its default whenever the preset
            // changes. Changed in the game's own settings window, that happens at once; changed in the mod manager while the
            // game is closed, at the next start, against the preset noted in a small file beside the settings.
            HudPreset.SettingChanged += delegate { ResetStyles("the preset was changed to " + HudPreset.Value); NotePreset(); };
            string lastPreset = "";
            try { if (System.IO.File.Exists(PresetNote)) lastPreset = System.IO.File.ReadAllText(PresetNote).Trim(); } catch (Exception) { }
            if (lastPreset != "" && lastPreset != HudPreset.Value) ResetStyles("the preset changed from " + lastPreset + " to " + HudPreset.Value + " since the last start");
            NotePreset();
            Config.Save();
            new Harmony(Guid).PatchAll(Assembly.GetExecutingAssembly());
#if DEV
            /* in-game check of the Jotunn clash (1.0.1): wakes Jotunn's input handling after the mod's own patches, as a Jotunn mod with key binds does */
            JotunnWake = Internal.Bind("Compatibility test", "WakeJotunnInput", false, "Developer build only: wake Jotunn's input handling at start, after this mod's own patches, as a Jotunn mod with key binds does. For checking that LT shortcuts do not also do the game's own action with Jotunn.");
            if (JotunnWake.Value) JotunnCheck.Wake();
#endif
            Log.LogInfo("Controller Support Plus " + Version + " loaded");
        }
#if DEV
        static ConfigEntry<bool> JotunnWake;
#endif
        static string PresetNote { get { return System.IO.Path.Combine(Paths.ConfigPath, Guid + ".lastpreset"); } }
        static void NotePreset() { try { System.IO.File.WriteAllText(PresetNote, HudPreset.Value); } catch (Exception) { } }
        static void ResetStyles(string why)
        {
            // ShowButtonGuide is global: kept across presets (0.69.2). PickUpNotificationStyle gets each preset's starting choice below (0.70.3).
            foreach (ConfigEntryBase e in new ConfigEntryBase[] { ResourceStyle, HotbarStyle, BottomBarSide, RpgMap, ShowMinimap, BuffStyle, BuffPos })
                if (e != null && !Equals(e.BoxedValue, e.DefaultValue)) e.BoxedValue = e.DefaultValue;
            // a preset's own starting choices that differ from the shared default: Bottom HUD starts with the row hotbar
            if (PresetBottom && HotbarStyle != null && HotbarStyle.Value != "Row") HotbarStyle.Value = "Row";
            // and the buffs and debuffs: Action RPG starts Slim in the top left (over the resource bars), Bottom HUD Compact
            if (PresetActionRpg) { if (BuffStyle != null) BuffStyle.Value = "Detailed"; if (BuffPos != null) BuffPos.Value = "TopLeft"; }   /* Detailed since 0.92.13 (Slim before) */
            if (PresetBottom && BuffStyle != null) BuffStyle.Value = "Detailed";
            // notifications: the Vanilla preset starts with the game's own, the others with the mod's (Modern)
            if (FeedPos != null) FeedPos.Value = HudPreset.Value == "Vanilla" ? "Vanilla" : "New";
            // and the equip progress: the Vanilla preset starts with the game's own bar, the others with the radial ring
            if (EquipProgress != null) EquipProgress.Value = HudPreset.Value == "Vanilla" ? "Vanilla" : "Ring";
            Log.LogInfo("HUD styles reset to their defaults: " + why);
        }

        public static ConfigEntry<bool> FavEnabled, GamePanelStyle, HideHotbar, StripBackground, FavSecondary, StackHudHints, FavMarkers;
        public static ConfigEntry<float> HudHintsGap, HudHintsSpacing, CursorSlotSpeed, CursorPanelSpeed, CursorTravelSpeed;
        public static ConfigEntry<bool> CursorEnabled, FavDimSprint, DimPanels, DotCrosshair, EquipRingLabel;
        public static ConfigEntry<string> EquipProgress, EquipRingFor;
        public static bool EquipRing { get { return EquipProgress != null && EquipProgress.Value == "Ring" && Enabled.Value && ZInput.IsGamepadActive(); } }
        public static ConfigEntry<bool> HudOnKbm, LtSelectSettings, SwapDpadActions, HudPickSeen, AutoPickup, LtDpadLayer;
        static ConfigFile Internal;   // the settings that are not in the mod manager (0.89.0)
        public static ConfigEntry<float> WheelHintsExtra, WheelReachSeen, HeaderSeen, BodySeen;
        // keyboard and mouse with a mod preset (Bottom HUD, Action RPG) applied to them: the gameplay hints stack as on the controller
        public static bool KbmPreset { get { return !ZInput.IsGamepadActive() && HudOnKbm != null && HudOnKbm.Value && HudPreset.Value != "Vanilla"; } }
        // the HUD preset's layout is in use: on the controller, and on keyboard and mouse when ApplyHudPresetToKeyboardAndMouse is on
        public static bool LayoutOn { get { return Enabled.Value && (ZInput.IsGamepadActive() || (HudOnKbm != null && HudOnKbm.Value)); } }
        public static ConfigEntry<string> HudPreset, ResourceStyle, HotbarStyle, BottomBarSide, RpgMap, FeedPos, BuffStyle, BuffPos;
        // the slim buffs and debuffs: Bottom HUD and Action RPG only, the Vanilla preset keeps the game's own
        public static bool SlimBuffs { get { return HudPreset != null && HudPreset.Value != "Vanilla" && BuffStyle != null && BuffStyle.Value != "Vanilla"; } }   // the mod draws them (Compact or Slim)
        public static ConfigEntry<bool> ShowMinimap, ShowMapHints;
        // Where everything goes comes from the preset alone. The styles are the player's preferences, used only where the
        // preset supports them (a preference the preset cannot use is kept for the presets that can).
        // The inventory is on screen covering the HUD: false while the Controller Support Plus Settings window shows the HUD instead.
        public static bool BagCovers { get { return InventoryGui.IsVisible() && !ModSettings.Open; } }
        public static bool PresetBottom { get { return HudPreset != null && HudPreset.Value == "BottomHUD"; } }
        public static bool PresetActionRpg { get { return HudPreset != null && HudPreset.Value == "ActionRPG"; } }
        public static bool RowHotbar { get { return HotbarStyle != null && HotbarStyle.Value == "Row"; } }
        public static bool CompactHotbar { get { return HotbarStyle != null && HotbarStyle.Value == "CompactCross"; } }
        public static string HotbarAt { get { return PresetBottom ? (BottomBarSide != null && BottomBarSide.Value == "Right" ? "Right" : "Center") : PresetActionRpg ? "Left" : "Right"; } }
        public static bool HotbarCentred { get { return HotbarAt == "Center"; } }
        public static bool HotbarLeft { get { return HotbarAt == "Left"; } }
        public static bool MinimapBottom { get { return PresetBottom || (PresetActionRpg && RpgMap != null && RpgMap.Value == "Bottom"); } }   // Bottom HUD: always bottom right, no choice
        // the game's own resource bars cannot go to the top left, so Action RPG always uses the Horizontal ones
        public static bool VitalsGrouped { get { return PresetActionRpg || (PresetBottom && ResourceStyle.Value != "Vanilla"); } }
        // Mixed: the group without stamina and eitr, which stay the game's own. Action RPG uses it unless Horizontal is chosen
        // (its default, since the shared setting starts at Vanilla, which Action RPG cannot use)
        public static bool VitalsMixed { get { return (PresetBottom && ResourceStyle.Value == "Mixed") || (PresetActionRpg && ResourceStyle.Value != "Horizontal"); } }
        public static bool VitalsBottomLeft { get { return VitalsGrouped && PresetBottom; } }
        public static ConfigEntry<bool> LtInSetMiddle;
        public static ConfigEntry<float> DimStrength, FavFlash, SneakHold, ChordWindow, ChordGrace, ScrollSpeed;
        public static ConfigEntry<bool> RemapDodge, PowerChord, ProLayout, HideHud, FavAssignSound, SheatheOnRs, ObjectSlots;
        public static ConfigEntry<float> FavScale, FavDownHold, HintPadding, GlyphSize, StripExtraWidth, RingFullBeat;

        private void Update()
        {
            Perf.Frame();
            Deposit.Tick();   // the button that closed the bag by itself: counts again once let go
            long t = Perf.Now();
            try { Favorites.WorldUpdate(); } catch (Exception e) { Core.Fail(e); }
            t = Perf.Lap("hotbar", t);
            try { Pro.Update(); Remap.Update(); } catch (Exception e) { Remap.Reset(); Core.Fail(e); }
            t = Perf.Lap("button remaps", t);
            try { BuildCursor.Update(); } catch (Exception e) { BuildCursor.Stop(); Core.Fail(e); }
            Perf.Lap("build cursor", t);
            try { AutoPickupLink.Tick(); } catch (Exception e) { Plugin.Log.LogWarning("Auto-pickup: " + e.Message); }
            try { JotunnCheck.Tick(); } catch (Exception e) { Plugin.Log.LogWarning("Jotunn check: " + e.Message); }
        }

        private void LateUpdate()
        {
            long t = Perf.Now();
            try { Hints.Arrange(); } catch (Exception e) { Core.Fail(e); }
            t = Perf.Lap("button guide", t);
            try { HudBehind.Tick(); } catch (Exception e) { Core.Fail(e); }
            try { PickupFeed.Tick(); } catch (Exception e) { Plugin.Log.LogWarning("Pickup feed: " + e.Message); }
            try { Landed.Tick(); } catch (Exception e) { Core.Fail(e); }
            try { HoldMark.Tick(); } catch (Exception e) { Core.Fail(e); }
            t = Perf.Lap("inventory extras", t);
#if DEV
            try { HudDump.Maybe(); } catch (Exception e) { Plugin.Log.LogWarning("HUD dump: " + e.Message); }
#endif
            try { MapPlace.Tick(); } catch (Exception e) { Plugin.Log.LogWarning("Minimap place: " + e.Message); }
            t = Perf.Lap("minimap", t);
            try { Vitals.Tick(); } catch (Exception e) { Vitals.Off(); Plugin.Log.LogWarning("Vitals: " + e.Message); }
            try { Buffs.Tick(); } catch (Exception e) { Buffs.Off(); Plugin.Log.LogWarning("Buffs: " + e.Message); }
            GameHotbar.Tick();
            try { ModSettings.MenuTick(); } catch (Exception e) { ModSettings.Close(); Plugin.Log.LogWarning("Settings button: " + e); }   /* the whole error, where it happened included (0.92.23) */
            try { ModSettings.LateTick(); } catch (Exception e) { ModSettings.Close(); Plugin.Log.LogWarning("Settings window: " + e.Message); }
            try { HudPick.LateCheck(); HudPick.Tick(); } catch (Exception e) { Plugin.Log.LogWarning("HUD pick: " + e.Message); }
            try { MessageNudge.Tick(); } catch (Exception e) { Plugin.Log.LogWarning("Message nudge: " + e.Message); }
            t = Perf.Lap("resource bars", t);
            // after the game's own scrolling has run this frame: lists held by a cursor go back to where they are meant to be
            try { if (Cursor.On || BuildCursor.On) Scroll.Hold(); else Scroll.Release(); } catch (Exception e) { Scroll.Release(); Core.Fail(e); }
            t = Perf.Lap("list scrolling", t);
#if DEV
            try { Shots.Tick(); } catch (Exception e) { Core.Fail(e); }
            Perf.Lap("dev", t);
#endif
        }

#if DEV
        public static ConfigEntry<bool> Captures, ExportKit;
        public static ConfigEntry<string> CaptureFolder, ExportFolder;
#endif
    }

    // ---------- developer tools: only in the developer build (DEV) ----------
    // The shared build (the zip that goes out, made by package.ps1 only when Rob asks) is compiled without DEV and with every
    // call to these taken out first, so it has no trace of them at all (0.89.0, Rob). package.ps1 checks the finished file.
#if DEV
    // ---------- design export ----------
    // Once per game session, the first time the inventory is open, saves the game's own UI art (one PNG per sprite),
    // the controller glyphs, and a spec file with sizes, 9-slice borders, colours, fonts and the layout of the inventory.
    // It exists so the UI can be rebuilt faithfully in a design tool. It only reads; nothing in the game is changed.
    static class Export
    {
        static bool done;
        static float visibleSince = -1f;
        static readonly System.Collections.Generic.Dictionary<Texture, Texture2D> readable = new System.Collections.Generic.Dictionary<Texture, Texture2D>();

        public static void Maybe(InventoryGui gui)
        {
            if (done || !Plugin.ExportKit.Value) return;
            if (!InventoryGui.IsVisible()) { visibleSince = -1f; return; }
            if (visibleSince < 0f) visibleSince = Time.unscaledTime;
            if (Time.unscaledTime - visibleSince < 1f) return;   // wait for the open animation
            done = true;
            try { Run(gui); } catch (Exception e) { Plugin.Log.LogError("Design export failed: " + e); }
            foreach (Texture2D t in readable.Values) UnityEngine.Object.Destroy(t);
            readable.Clear();
        }

        static string Esc(string s) { return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", " "); }
        static string Num(float f) { return f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
        static string Col(Color c) { return "[" + Num(c.r) + "," + Num(c.g) + "," + Num(c.b) + "," + Num(c.a) + "]"; }
        static string Safe(string name) { foreach (char ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_'); return name.Replace(' ', '_'); }

        static Texture2D Readable(Texture tex)
        {
            Texture2D copy;
            if (readable.TryGetValue(tex, out copy)) return copy;
            RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            readable[tex] = copy;
            return copy;
        }

        static bool SavePng(Texture tex, int x, int y, int w, int h, string file)
        {
            if (tex == null || w <= 0 || h <= 0) return false;
            Texture2D src = Readable(tex);
            Texture2D cut = new Texture2D(w, h, TextureFormat.RGBA32, false);
            cut.SetPixels(src.GetPixels(x, y, w, h));
            cut.Apply();
            System.IO.File.WriteAllBytes(file, ImageConversion.EncodeToPNG(cut));
            UnityEngine.Object.Destroy(cut);
            return true;
        }

        static void Run(InventoryGui gui)
        {
            string dir = Plugin.ExportFolder.Value, glyphDir = System.IO.Path.Combine(dir, "glyphs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.Directory.CreateDirectory(glyphDir);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Canvas canvas = gui.GetComponentInParent<Canvas>();
            Canvas rootCanvas = canvas != null ? canvas.rootCanvas : null;
            float scale = rootCanvas != null ? rootCanvas.transform.lossyScale.x : 1f;
            if (scale <= 0f) scale = 1f;

            // roots to describe: the whole inventory screen, one inventory slot, one hotbar slot
            System.Collections.Generic.List<Transform> roots = new System.Collections.Generic.List<Transform>();
            System.Collections.Generic.List<string> rootNames = new System.Collections.Generic.List<string>();
            roots.Add(gui.transform); rootNames.Add("inventory_screen");
            if (gui.m_playerGrid != null && gui.m_playerGrid.m_elementPrefab != null) { roots.Add(gui.m_playerGrid.m_elementPrefab.transform); rootNames.Add("inventory_slot"); }
            HotkeyBar bar = Hud.instance != null ? Hud.instance.GetComponentInChildren<HotkeyBar>(true) : null;
            if (bar != null && bar.m_elementPrefab != null) { roots.Add(bar.m_elementPrefab.transform); rootNames.Add("hotbar_slot"); }

            sb.Append("{\n\"note\": \"Sizes are in the game's UI units. x,y are the top-left corner measured from the top-left of the screen, for inventory_screen only; the two slot trees use local position instead.\",\n");
            sb.Append("\"screen\": {\"pixelWidth\": " + Screen.width + ", \"pixelHeight\": " + Screen.height + ", \"uiScale\": " + Num(scale) + ", \"unitsWide\": " + Num(Screen.width / scale) + ", \"unitsHigh\": " + Num(Screen.height / scale) + "},\n");

            // ----- sprites -----
            System.Collections.Generic.Dictionary<Sprite, string> files = new System.Collections.Generic.Dictionary<Sprite, string>();
            System.Collections.Generic.HashSet<string> used = new System.Collections.Generic.HashSet<string>();
            sb.Append("\"sprites\": [\n");
            bool first = true; int saved = 0, failed = 0;
            foreach (Transform root in roots)
                foreach (Image im in root.GetComponentsInChildren<Image>(true))
                {
                    Sprite sp = im.sprite;
                    if (sp == null || files.ContainsKey(sp)) continue;
                    string name = Safe(sp.name);
                    while (!used.Add(name)) name += "_";
                    files[sp] = name + ".png";
                    try
                    {
                        Rect r = sp.textureRect;
                        SavePng(sp.texture, Mathf.RoundToInt(r.x), Mathf.RoundToInt(r.y), Mathf.RoundToInt(r.width), Mathf.RoundToInt(r.height), System.IO.Path.Combine(dir, name + ".png"));
                        Vector4 b = sp.border;
                        sb.Append((first ? "" : ",\n") + "  {\"name\": \"" + Esc(sp.name) + "\", \"file\": \"" + name + ".png\", \"width\": " + Num(r.width) + ", \"height\": " + Num(r.height)
                            + ", \"border\": {\"left\": " + Num(b.x) + ", \"bottom\": " + Num(b.y) + ", \"right\": " + Num(b.z) + ", \"top\": " + Num(b.w) + "}, \"pixelsPerUnit\": " + Num(sp.pixelsPerUnit) + "}");
                        first = false; saved++;
                    }
                    catch (Exception e) { failed++; Plugin.Log.LogWarning("Design export: sprite " + sp.name + " skipped: " + e.Message); }
                }
            sb.Append("\n],\n");

            // ----- controller glyphs -----
            sb.Append("\"glyphs\": [\n");
            first = true; int glyphCount = 0;
            foreach (TMP_SpriteAsset asset in Resources.FindObjectsOfTypeAll<TMP_SpriteAsset>())
            {
                if (asset == null || asset.spriteSheet == null || asset.spriteCharacterTable == null) continue;
                string sub = System.IO.Path.Combine(glyphDir, Safe(asset.name));
                System.IO.Directory.CreateDirectory(sub);
                foreach (TMP_SpriteCharacter ch in asset.spriteCharacterTable)
                {
                    try
                    {
                        if (ch == null || ch.glyph == null) continue;
                        UnityEngine.TextCore.GlyphRect g = ch.glyph.glyphRect;
                        string file = Safe(ch.name) + ".png";
                        SavePng(asset.spriteSheet, g.x, g.y, g.width, g.height, System.IO.Path.Combine(sub, file));
                        sb.Append((first ? "" : ",\n") + "  {\"set\": \"" + Esc(asset.name) + "\", \"name\": \"" + Esc(ch.name) + "\", \"file\": \"glyphs/" + Safe(asset.name) + "/" + file + "\", \"width\": " + g.width + ", \"height\": " + g.height + "}");
                        first = false; glyphCount++;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Design export: glyph skipped: " + e.Message); }
                }
            }
            sb.Append("\n],\n");

            // ----- fonts in use -----
            System.Collections.Generic.Dictionary<string, int> fonts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (TMP_Text t in gui.GetComponentsInChildren<TMP_Text>(true))
            {
                string key = "{\"font\": \"" + Esc(t.font != null ? t.font.name : "none") + "\", \"size\": " + Num(t.fontSize) + ", \"style\": \"" + t.fontStyle + "\", \"color\": " + Col(t.color);
                int n; fonts.TryGetValue(key, out n); fonts[key] = n + 1;
            }
            sb.Append("\"fonts\": [\n");
            first = true;
            foreach (System.Collections.Generic.KeyValuePair<string, int> kv in fonts) { sb.Append((first ? "" : ",\n") + "  " + kv.Key + ", \"uses\": " + kv.Value + "}"); first = false; }
            sb.Append("\n],\n");

            // ----- layout -----
            sb.Append("\"layout\": {\n");
            Vector3[] corners = new Vector3[4];
            for (int ri = 0; ri < roots.Count; ri++)
            {
                sb.Append("\"" + rootNames[ri] + "\": [\n");
                bool onScreen = ri == 0; first = true; int count = 0;
                foreach (RectTransform rt in roots[ri].GetComponentsInChildren<RectTransform>(true))
                {
                    if (count >= 2500) break;
                    int depth = 0;
                    string path = rt.name;
                    for (Transform t = rt.parent; t != null && rt != roots[ri] && depth < 20; t = t.parent) { depth++; path = t.name + "/" + path; if (t == roots[ri]) break; }
                    if (depth > 9 || rt.name.StartsWith("ControllerPro_") || path.Contains("/ControllerPro_")) continue;
                    Image im = rt.GetComponent<Image>();
                    TMP_Text tx = rt.GetComponent<TMP_Text>();
                    string line = "  {\"path\": \"" + Esc(path) + "\", \"depth\": " + depth + ", \"active\": " + (rt.gameObject.activeInHierarchy ? "true" : "false") + ", \"w\": " + Num(rt.rect.width) + ", \"h\": " + Num(rt.rect.height);
                    if (onScreen)
                    {
                        rt.GetWorldCorners(corners);
                        line += ", \"x\": " + Num(corners[1].x / scale) + ", \"y\": " + Num((Screen.height - corners[1].y) / scale);
                    }
                    else line += ", \"localX\": " + Num(rt.anchoredPosition.x) + ", \"localY\": " + Num(rt.anchoredPosition.y);
                    if (im != null) line += ", \"image\": {\"sprite\": \"" + Esc(im.sprite != null ? im.sprite.name : "") + "\", \"file\": \"" + (im.sprite != null && files.ContainsKey(im.sprite) ? files[im.sprite] : "") + "\", \"type\": \"" + im.type + "\", \"color\": " + Col(im.color) + ", \"shown\": " + (im.enabled ? "true" : "false") + "}";
                    if (tx != null) line += ", \"text\": {\"font\": \"" + Esc(tx.font != null ? tx.font.name : "") + "\", \"size\": " + Num(tx.fontSize) + ", \"style\": \"" + tx.fontStyle + "\", \"align\": \"" + tx.alignment + "\", \"color\": " + Col(tx.color) + ", \"content\": \"" + Esc(tx.text != null && tx.text.Length > 60 ? tx.text.Substring(0, 60) : tx.text) + "\"}";
                    sb.Append((first ? "" : ",\n") + line + "}");
                    first = false; count++;
                }
                sb.Append("\n]" + (ri < roots.Count - 1 ? "," : "") + "\n");
            }
            sb.Append("}\n}\n");

            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "spec.json"), sb.ToString(), new System.Text.UTF8Encoding(false));
            Plugin.Log.LogInfo("Design export: " + saved + " sprites (" + failed + " skipped), " + glyphCount + " glyphs, spec.json written to " + dir);
        }
    }

    // ---------- design captures ----------
    // Saves a screenshot of each mod UI state the first time it appears (and again each time it is re-entered),
    // one file per state, so the current look of the mod can be reviewed outside the game.
    static class Shots
    {
        static string wanted, current;
        static float dueAt;
        static bool done;
        // Each state is captured once per game session. Taking a screenshot stalls the game for a moment, and capturing
        // every time a state came back (every hold, every time the bag opened) made play stutter.
        static readonly HashSet<string> taken = new HashSet<string>();

        public static void Want(string state) { if (state != "hold-ring") wanted = state; }   // the hold ring is mid-animation: never captured

        public static void Tick()
        {
            string w = wanted; wanted = null;
            if (w == null || !Plugin.Captures.Value) { current = null; return; }
            float now = Time.unscaledTime;
            if (w != current) { current = w; done = taken.Contains(w); dueAt = now + 0.6f; } // let open animations settle
            if (done || now < dueAt) return;
            done = true;
            taken.Add(w);
            string dir = Plugin.CaptureFolder.Value;
            System.IO.Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, w + ".png"));
        }
    }
#endif

    // ---------- Show button hints: the mod's setting and the game's own are one (1.0.0, Rob) ----------
    // The game keeps its "Show button hints" (Settings > Gameplay) as the KeyHints preference and applies it through
    // KeyHints.ApplySettings. A change in the mod's window writes that preference and applies it; whenever the game applies
    // it (at start, after its Settings screen's OK), the mod's setting takes the game's value.
    static class HintsLink
    {
        public static void FromMod()
        {
            int v = Plugin.ShowMapHints.Value ? 1 : 0;
            if (PlatformPrefs.GetInt("KeyHints", 1) == v) return;
            PlatformPrefs.SetInt("KeyHints", v);
            try { PlatformPrefs.Save(); } catch (Exception e) { Plugin.Log.LogWarning("Button hints: the game's setting not saved: " + e.Message); }
            if (KeyHints.instance != null) KeyHints.instance.ApplySettings();
        }
        public static void FromGame()
        {
            bool on = PlatformPrefs.GetInt("KeyHints", 1) == 1;
            if (Plugin.ShowMapHints != null && Plugin.ShowMapHints.Value != on) Plugin.ShowMapHints.Value = on;
        }
    }
    // ---------- Auto-pickup: the mod's setting and the game's own switch are one (1.0.1, Rob) ----------
    // The game keeps auto-pickup in one switch (Player.m_enableAutoPickup, on at every start) and flips it with its toggle
    // key, or block + left stick on the controller. The setting in the window sets that switch; a flip by the game's own
    // key (keyboard, or the chord in the game's own layouts) is taken into the setting, so both always agree. On the
    // Controller Support Plus layout the chord is off (left stick is sprint there, so sprinting while blocking flipped it).
    static class AutoPickupLink
    {
        static readonly FieldInfo F_on = AccessTools.Field(typeof(Player), "m_enableAutoPickup");
        static bool seen = true, logged;
        public static void Tick()
        {
            if (F_on == null || Plugin.AutoPickup == null) return;
            bool game = (bool)F_on.GetValue(null);
            if (game != seen) { seen = game; if (Plugin.AutoPickup.Value != game) Plugin.AutoPickup.Value = game; return; }   // the game flipped it
            if (game != Plugin.AutoPickup.Value)
            {
                F_on.SetValue(null, Plugin.AutoPickup.Value); seen = Plugin.AutoPickup.Value;
                if (!logged) { logged = true; Plugin.Log.LogInfo("Auto-pickup: the game's switch set " + (seen ? "on" : "off") + " from the setting"); }
            }
        }
        // the chord is the game's own in its own layouts; on the Controller Support Plus layout it does nothing
        public static bool ChordOff { get { return Pro.On; } }
    }

    // ---------- Jotunn (1.0.1) ----------
    // Jotunn's input handling, once a Jotunn mod with key binds wakes it, adds its own postfix to the game's button reads,
    // and that postfix sets the result back to the game's raw value. Run after the mod's, it undid every press the mod
    // holds back (LT + D-pad down also sat, LT + A also jumped). The mod's postfixes are ordered after Jotunn's
    // (HarmonyAfter); without Jotunn, or with its input handling asleep, nothing changes. Once in a world, the order the
    // game's button read is patched in goes to the log, to check.
    static class JotunnCheck
    {
        static bool logged;
#if DEV
        public static void Wake()
        {
            Type t = AccessTools.TypeByName("Jotunn.Managers.InputManager");
            PropertyInfo inst = t != null ? AccessTools.Property(t, "Instance") : null;
            if (inst == null) { Plugin.Log.LogInfo("Jotunn: not installed, nothing to wake"); return; }
            inst.GetValue(null, null);
            Plugin.Log.LogInfo("Jotunn: its input handling woken after this mod's patches");
        }
#endif
        public static void Tick()
        {
            if (logged || Player.m_localPlayer == null) return;
            logged = true;
            Patches info = Harmony.GetPatchInfo(AccessTools.Method(typeof(ZInput), "GetButtonDown", new[] { typeof(string) }));
            if (info == null) return;
            bool jotunn = false;
            foreach (Patch p in info.Postfixes) if (p.owner == Plugin.JotunnGuid) jotunn = true;
            if (!jotunn) return;   /* nothing to report without Jotunn's input handling */
            List<string> owners = new List<string>();
            foreach (Patch p in info.Postfixes) owners.Add(p.owner + (p.after != null && p.after.Length > 0 ? " (after " + string.Join(", ", p.after) + ")" : ""));
            Plugin.Log.LogInfo("Jotunn: its input handling is on; the game's button reads are patched by (in the order added): " + string.Join(" | ", owners.ToArray()) + ". This mod's are set to run after Jotunn's, so the presses it holds back stay held back");
        }
    }

    [HarmonyPatch(typeof(KeyHints), "ApplySettings")]
    static class Patch_KeyHintsApply
    {
        static void Postfix() { try { HintsLink.FromGame(); } catch (Exception e) { Plugin.Log.LogWarning("Button hints: " + e.Message); } }
    }

    // ---------- input suppression ----------
    // Vanilla reads buttons through ZInput. While the game's own inventory code runs, these patches
    // hide the presses the mod has taken over. The mod reads the real values after vanilla is done.
    [HarmonyPatch(typeof(ZInput), "GetButtonDown")]
    static class Patch_GetButtonDown
    {
        /* after Jotunn's own postfix (1.0.1): it sets the result back to the game's raw value, so ours must come last */
        [HarmonyAfter(Plugin.JotunnGuid)]
        static void Postfix(string name, ref bool __result)
        {
            if (Core.Bypass) return;   // the mod's own reads see the real value
            if (ModSettings.Blocking && name.StartsWith("Joy")) { __result = false; return; }
            if (__result && name == "JoyAutoPickup" && AutoPickupLink.ChordOff) { __result = false; return; }   /* 1.0.1: block + sprint no longer flips auto-pickup on the Controller Support Plus layout; the setting does it */
            if (Core.PadRadialLt && name == "JoyRadial") { __result = false; return; }
            if (name == "JoyMap" && ModSettings.MenuButtonUp) { __result = false; return; }
            if ((name == "JoyChat" || name == "JoyMap") && ModSettings.LtSelectChord) { __result = false; return; }   /* LT + Select opens Controller Support Plus Settings, not the chat */   /* Select opens Controller Support Plus Settings from the pause menu, not the map */   // nothing behind the settings window reacts
            if (name == "JoyMenu" && __result && Favorites.QuickChord) { __result = false; return; }   // LT + Start opens the Controller Hotbar window, not the menu
            if (Cursor.On)
            {
                // cursor mode: the left stick steers the cursor, so it must not also step through cells or lists
                if (name.Length > 9 && name[3] == 'L' && name[4] == 'S') { __result = false; return; }
                // and while the cursor is off the grid, A belongs to the cursor's own click
                if (Cursor.OffGrid && name == "JoyButtonA") { __result = false; return; }
            }
            // Start saves in the assign overlay, so it must not also open the pause menu
            if ((Core.Assigning || Time.frameCount == Core.AssignEndFrame) && (name == "JoyMenu" || name == "JoyStart" || name == "JoyToggleHUD" || name == "JoyTabLeft" || name == "JoyTabRight" || name == "JoyLBumper" || name == "JoyRBumper")) { __result = false; return; }   // while the window is open the inventory behind it does not switch panels
            if (Core.FavLT && Core.TakenByFavorites(name)) { __result = false; return; }
            if (Core.FavFace && Core.InGuiUpdate && name == "JoyButtonY") { __result = false; return; }   // LT + Y is a favorite, not "open inventory"
            if (HudPick.TakesY && Core.InGuiUpdate && name == "JoyButtonY") { __result = false; return; }   // the first-launch plaque is up: Y opens the HUD pick, not the inventory
            if (__result && Deposit.Swallow(name)) { __result = false; return; }   // the button that closed the bag by itself is still down
            if (Core.FavWorld)
            {
                // favorites own the D-pad in the world: hotbar cycling is retired
                if (name == "JoyHotbarLeft" || name == "JoyHotbarRight" || name == "JoyHotbarUse") { __result = false; return; }
                if (Core.DownOwned)
                {
                    if (Core.DownIsGP && name == "JoyGP") { __result = Core.TakeInject(); return; }
                    if (!Core.DownIsGP && name == "JoySit") { __result = false; return; }
                }
            }
            if (!__result) return;
            if (Core.InGuiUpdate)
            {
                if (Core.SuppressY && name == "JoyButtonY") { __result = false; return; }
                if (Core.SuppressB && name == "JoyButtonB") { __result = false; return; }
            }
            if (Core.InGridUpdate && (Core.SuppressGridAll || (Core.SuppressA && name == "JoyButtonA") || (Core.SuppressAX && (name == "JoyButtonA" || name == "JoyButtonX")) || (Core.SuppressX && name == "JoyButtonX"))) __result = false;
        }
    }

    [HarmonyPatch(typeof(ZInput), "GetButtonUp")]
    static class Patch_GetButtonUp
    {
        /* after Jotunn's own postfix (1.0.1): it sets the result back to the game's raw value, so ours must come last */
        [HarmonyAfter(Plugin.JotunnGuid)]
        static void Postfix(string name, ref bool __result)
        {
            if (!Core.Bypass && ModSettings.Blocking && name.StartsWith("Joy")) { __result = false; return; }
            if (!Core.Bypass && Core.PadRadialLt && name == "JoyRadial") { __result = false; return; }
            if (Core.FavLT && Core.TakenByFavorites(name)) { __result = false; return; }
            if (__result && !Core.Bypass && Deposit.Swallow(name)) { __result = false; return; }   // letting go of the button that closed the bag by itself
            if (Core.FavWorld && Core.DownOwned && !Core.DownIsGP && name == "JoySit") __result = Core.TakeInject();
        }
    }

    [HarmonyPatch(typeof(ZInput), "GetButtonPressedTimer")]
    static class Patch_GetButtonPressedTimer
    {
        // the radial opens after LT has been held for a moment; with LT as the favorites modifier that timer reads as zero
        static void Postfix(string name, ref float __result)
        {
            if (Core.FavLT && name == "JoyRadial") __result = 0f;
        }
    }

    [HarmonyPatch(typeof(ZInput), "GetButton")]
    static class Patch_GetButton
    {
        /* after Jotunn's own postfix (1.0.1): it sets the result back to the game's raw value, so ours must come last */
        [HarmonyAfter(Plugin.JotunnGuid)]
        static void Postfix(string name, ref bool __result)
        {
            if (!__result || Core.Bypass) return;
            if (ModSettings.Blocking && name.StartsWith("Joy")) { __result = false; return; }
            if (Core.PadRadialLt && name == "JoyRadial") { __result = false; return; }
            if (name == "JoyMap" && ModSettings.MenuButtonUp) { __result = false; return; }
            if ((name == "JoyChat" || name == "JoyMap") && ModSettings.LtSelectChord) { __result = false; return; }   /* LT + Select opens Controller Support Plus Settings, not the chat */   /* Select opens Controller Support Plus Settings from the pause menu, not the map */
            if (Remap.NoCrouch && name == "JoyCrouch") { __result = false; return; }   // PRO: the right stick click sheathes
            if (Deposit.Swallow(name)) { __result = false; return; }   // the button that closed the bag by itself is still down
            if (Cursor.On && name.Length > 9 && name[3] == 'L' && name[4] == 'S') { __result = false; return; }
            if (Core.FavLT && Core.TakenByFavorites(name)) { __result = false; return; }
            if (Core.InGridUpdate && Core.SuppressTriggers && (name == "JoyLTrigger" || name == "JoyRTrigger")) { __result = false; return; }
            if (Core.FavWorld && Core.DownOwned && !Core.DownIsGP && name == "JoySit") __result = false;
        }
    }

    [HarmonyPatch(typeof(InventoryGrid), "UpdateGamepad")]
    static class Patch_GridGamepad
    {
        static void Prefix() { Core.InGridUpdate = true; }
        static void Finalizer() { Core.InGridUpdate = false; }
    }

    [HarmonyPatch(typeof(InventoryGui), "Update")]
    static class Patch_GuiUpdate
    {
        static void Prefix(InventoryGui __instance)
        {
            try { Core.BeginFrame(__instance); } catch (Exception e) { Core.Fail(e); }
            Core.InGuiUpdate = true;
        }
        static void Postfix(InventoryGui __instance)
        {
            Core.InGuiUpdate = false;
            long t = Perf.Now();
            try { Core.Process(__instance); } catch (Exception e) { Core.Fail(e); }
            Perf.Lap("inventory", t);
#if DEV
            Export.Maybe(__instance);
#endif
        }
        static void Finalizer() { Core.InGuiUpdate = false; }
    }

    // ---------- how long the mod's own work takes ----------
    // Each part of the mod's per-frame work is timed. Only two things are written to the log: a hitch (a frame that took
    // the game over 50 ms), with how much of that frame was the mod's and which part took most (in the shareable build
    // only when the mod's part was 5 ms or more); and, in the developer build, once a minute of play, the mod's average
    // and worst cost per frame. Enough to tell whether a stutter is the mod's, with no cost to speak of.
    static class Perf
    {
        static readonly double ToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        static double frameMs, worstMs, minuteSum, minuteWorst;
        static string worstPart = "-", minuteWorstPart = "-";
        static int frames;
        static float minuteFrom = -1f;
        public static long Now() { return System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static long Lap(string part, long from)
        {
            long now = Now();
            double ms = (now - from) * ToMs;
            frameMs += ms;
            if (ms > worstMs) { worstMs = ms; worstPart = part; }
            return now;
        }
        // called once at the start of each frame: closes the frame before it
        public static void Frame()
        {
            float now = Time.unscaledTime;
            float gameMs = Time.unscaledDeltaTime * 1000f;
#if DEV
            const double Share = 0.0;
#else
            const double Share = 5.0;   /* V1: a player's log notes a hitch only when the mod's own part of it was real (5 ms or more) */
#endif
            if (gameMs > 50f && minuteFrom >= 0f && frameMs >= Share)
                Plugin.Log.LogInfo("Performance: hitch, the frame took " + gameMs.ToString("0") + " ms; Controller Support Plus's share " + frameMs.ToString("0.00") + " ms (most in " + worstPart + ": " + worstMs.ToString("0.00") + " ms)");
            minuteSum += frameMs; frames++;
            if (frameMs > minuteWorst) { minuteWorst = frameMs; minuteWorstPart = worstPart; }
            if (minuteFrom < 0f) minuteFrom = now;
            else if (now - minuteFrom >= 60f && frames > 0)
            {
#if DEV
                /* the minute's summary: developer build only (V1) */
                Plugin.Log.LogInfo("Performance (last minute): Controller Support Plus averaged " + (minuteSum / frames).ToString("0.00") + " ms per frame, worst " + minuteWorst.ToString("0.00") + " ms (" + minuteWorstPart + "), over " + frames + " frames");
#endif
                minuteFrom = now; minuteSum = 0; minuteWorst = 0; frames = 0;
            }
            frameMs = 0; worstMs = 0; worstPart = "-";
        }
    }

    // The game's own equip bar, hidden on the controller while the crosshair ring shows the progress instead. The game
    // sets it again on every update, so it comes back by itself with the Bar setting or on keyboard and mouse.
    [HarmonyPatch(typeof(Hud), "UpdateActionProgress")]
    static class Patch_ActionBar
    {
        static void Postfix(Hud __instance)
        {
            if (Plugin.EquipRing && __instance.m_actionBarRoot != null && __instance.m_actionBarRoot.activeSelf) __instance.m_actionBarRoot.SetActive(false);
        }
    }
    // Read by name by the in-game configuration manager (F1): Order puts a setting first in its section.


    // ---------- dot crosshair ----------
    // On the controller the game's cross becomes a small dot. It is the same object, so it still turns yellow over
    // something that can be used, hides when the game hides it, and the bow keeps its own aiming circle. With keyboard
    // or mouse the game's cross comes back.
    [HarmonyPatch(typeof(Hud), "UpdateCrosshair")]
    static class Patch_Crosshair
    {
        const float Dot = 6f;
        static Sprite cross; static Vector2 crossSize; static bool saved, dotted;
        static void Postfix(Hud __instance)
        {
            try
            {
                Image img = __instance.m_crosshair;
                if (img == null) return;
                RectTransform rt = img.rectTransform;
                if (!saved) { cross = img.sprite; crossSize = rt.sizeDelta; saved = true; }
                bool want = Plugin.Enabled.Value && Plugin.DotCrosshair.Value && ZInput.IsGamepadActive();
                if (want != dotted)
                {
                    dotted = want;
                    img.sprite = want ? Hints.Disc() : cross;
                    rt.sizeDelta = want ? new Vector2(Dot, Dot) : crossSize;
                }
                long t = Perf.Now();
                Progress(__instance, rt);
                Perf.Lap("crosshair ring", t);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Crosshair: " + e.Message); }
        }

        // ----- action progress around the crosshair -----
        // While the game runs a timed action (equipping or unequipping a weapon, tool or armour, reloading a crossbow), a
        // ring around the crosshair fills clockwise from the top as the action goes, in the hold ring's look: a faint
        // track and a white fill. It reads the game's own progress, the one its action bar shows, and appears for the
        // same actions (those longer than half a second). Finished, it stays full for a moment, as the hold ring does.
        // Controller only, like the dot. A setting picks the ring or the game's own bar, and whether the ring shows for
        // every timed action or only the ones the game would show its bar for.
        const float RingSize = 26f, Linger = 0.15f;
        static GameObject ring;
        static Image ringTrack, ringFill;
        static float lastProgress, fullUntil = -1f;
        static bool dotHidden;
        const float LabelGap = 10f, LabelFade = 0.15f;
        static float labelAlpha = 1f;
        static TMP_Text ringText;
        static string label = "", shownLabel = "";
        static void Progress(Hud h, RectTransform cross)
        {
            Player p = Player.m_localPlayer;
            float k = -1f;
            if (Plugin.EquipRing && p != null && cross.gameObject.activeInHierarchy)
            {
                string name; float progress; Player.MinorActionData data;
                p.GetActionProgress(out name, out progress, out data);
                float shortest = Plugin.EquipRingFor.Value == "VanillaBarsOnly" ? 0.5f : 0f;   // the game shows its bar only past half a second
                if (!string.IsNullOrEmpty(name) && data != null && data.m_duration > shortest)
                {
                    k = Mathf.Clamp01(progress);
                    // the action's name beside the ring, as the game's own bar names it, and only where the game would
                    // show that bar (actions over half a second); quick ones get the ring alone
                    label = data.m_duration > 0.5f && Localization.instance != null ? Localization.instance.Localize(name) : "";
                }
            }
            float now = Time.unscaledTime;
            if (k < 0f && lastProgress > 0.85f) fullUntil = now + Linger;   // just finished: shown full for a moment
            lastProgress = k;
            bool full = k < 0f && now < fullUntil;
            bool on = k >= 0f || full;
            if (ring == null)
            {
                if (!on) return;
                Vector2 mid = new Vector2(0.5f, 0.5f);
                ring = Hints.New("ControllerPro_ActionRing", cross.parent);
                Hints.Place(ring, mid, mid, mid, Vector2.zero, new Vector2(RingSize, RingSize));
                ringTrack = Hints.Img(Hints.New("track", ring.transform), Hints.Ring(), new Color(1f, 1f, 1f, 0.12f));   // fainter than the hold ring's track: it sits over the world
                Hints.Place(ringTrack.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                ringFill = Hints.Img(Hints.New("fill", ring.transform), Hints.Ring(), Color.white);
                Hints.Place(ringFill.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                ringFill.type = Image.Type.Filled; ringFill.fillMethod = Image.FillMethod.Radial360;
                ringFill.fillOrigin = (int)Image.Origin360.Top; ringFill.fillClockwise = true;
                ringTrack.raycastTarget = ringFill.raycastTarget = false;
                // the label: the game's equip bar text, to the right of the ring, in the look of the game's hover text by
                // the crosshair (what it says over a chest or an item on the ground): its font, size, colour and outline
                TMP_Text like = h.m_hoverName != null ? h.m_hoverName : h.m_actionName;
                if (like != null)
                {
                    Vector2 lm = new Vector2(0f, 0.5f);
                    ringText = Hints.Text(Hints.New("label", ring.transform), like, like.fontSize, like.color, TextAlignmentOptions.MidlineLeft);
                    if (ringText.font != null) ringText.fontSharedMaterial = ringText.font.material;   // the plain face: no dark outline, more subtle
                    ringText.enableWordWrapping = false; ringText.raycastTarget = false;
                    Hints.Place(ringText.gameObject, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), lm, new Vector2(LabelGap, 0f), new Vector2(400f, RingSize));
                }
            }
            if (ringText != null)
            {
                if (k >= 0f) shownLabel = label;   // kept through the moment the ring stays full
                bool txt = on && Plugin.EquipRingLabel.Value && !string.IsNullOrEmpty(shownLabel);
                if (ringText.gameObject.activeSelf != txt) ringText.gameObject.SetActive(txt);
                if (txt && ringText.text != shownLabel) ringText.text = shownLabel;
                // it steps aside for the game's own prompt by the crosshair (a chest, an item on the ground...): fades out
                // while one shows, and back in after
                TMP_Text hover = h.m_hoverName;
                bool prompt = hover != null && hover.gameObject.activeInHierarchy && hover.enabled && !string.IsNullOrEmpty(hover.text);
                labelAlpha = Mathf.MoveTowards(labelAlpha, prompt ? 0f : 1f, Time.unscaledDeltaTime / LabelFade);
                if (txt) ringText.alpha = labelAlpha;
            }
            if (ring.activeSelf != on) ring.SetActive(on);
            // while the ring shows it is the crosshair on its own: the dot is made see-through (its renderer's own
            // alpha, which the game never sets, so the game's colour and show/hide of the crosshair are untouched)
            Image dot = h.m_crosshair;
            if (dot != null && dotHidden != on) { dotHidden = on; dot.canvasRenderer.SetAlpha(on ? 0f : 1f); }
            if (!on) return;
            ring.transform.position = cross.position;   // centred on the crosshair, wherever the game has it
            ringFill.fillAmount = full ? 1f : k;
        }
    }

    /* while Controller Support Plus Settings is up in play (LT + Select), the character and camera stay still: the sticks step the window */
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    static class Patch_TakeInput
    {
        static void Postfix(ref bool __result) { if (ModSettings.Open || HudPick.Open) __result = false; }
    }

    [HarmonyPatch(typeof(KeyHints), "UpdateHints")]
    static class Patch_KeyHints
    {
        static void Postfix(KeyHints __instance)
        {
            long t = Perf.Now();
            // behind the Controller Support Plus Settings window the HUD shows as in play, the button hints with it (the game hides
            // them while the inventory is open)
            if (ModSettings.Open && Plugin.ShowMapHints.Value)
            {
                if (__instance.m_combatHints != null && !__instance.m_combatHints.activeSelf) __instance.m_combatHints.SetActive(true);
                if (__instance.m_inventoryHints != null) __instance.m_inventoryHints.SetActive(false);
                if (__instance.m_inventoryWithContainerHints != null) __instance.m_inventoryWithContainerHints.SetActive(false);
            }
            try { HudHints.ApplyAll(__instance.m_combatHints, true); HudHints.ApplyAll(__instance.m_buildHints, false); HudHints.ApplyAll(__instance.m_radialHints, false, true); /* the radial's own hints: beside the wheel (0.87.9) */ }
            catch (Exception e) { Plugin.Log.LogWarning("HUD hints: " + e.Message); }
            Perf.Lap("gameplay hints", t);
            // the button guide under the minimap switched off in the settings: the game shows it again each update, so
            // it is put away again each time (controller only, as the rest of the HUD layout)
            if (Plugin.Enabled.Value && (ZInput.IsGamepadActive() || Plugin.KbmPreset) && !Plugin.ShowMapHints.Value)
            {
                if (__instance.m_combatHints != null && __instance.m_combatHints.activeSelf) __instance.m_combatHints.SetActive(false);
                if (__instance.m_buildHints != null && __instance.m_buildHints.activeSelf) __instance.m_buildHints.SetActive(false);
                /* and the radial wheel's own hints (0.91.17) */
                if (__instance.m_radialHints != null && __instance.m_radialHints.activeSelf) __instance.m_radialHints.SetActive(false);
            }
            /* with the bag open on the pad the game's own hint rows never show, not even for the frame before the strip is built */
            bool bagPad = Plugin.Enabled.Value && ZInput.IsGamepadActive() && InventoryGui.IsVisible();
            if (!(Core.StripShowing || bagPad) || !Plugin.HideVanillaHints.Value) return;
            if (__instance.m_inventoryHints != null) __instance.m_inventoryHints.SetActive(false);
            if (__instance.m_inventoryWithContainerHints != null) __instance.m_inventoryWithContainerHints.SetActive(false);
        }
    }

    // ---------- controller cursor ----------
    // Appears the moment the left stick is used inside the inventory, and goes away when the D-pad is used, so the two
    // ways of getting around never fight. The cursor is drawn by the mod and is not the mouse: the game still sees a
    // controller. Whatever slot the cursor is over becomes the focused slot, so every other feature (A to move, the
    // holds, the LT layer, the button guide) works unchanged. Over a normal button (a recipe, Craft, a tab) A presses it.
    // Carrying an item outside every panel and pressing A drops it.
    // ---------- the cursor's hover look ----------
    // Every controller cursor (inventory, build menu, Controller Hotbar window) is a white ring over a soft dark ring. At
    // rest it is the larger ring with no dot. Over something it can act on (an item, a button, a piece, a square with
    // something on it) the rings close in to their smaller size and the dot appears in the middle, easing in and out, so
    // it is plain the cursor is on something. A hold ring in progress takes the ring's size at that moment.
    static class CursorLook
    {
        const float Grow = 0.25f, Ease = 0.1f;   // rest: 26 across grown to about 32; hover: 26, in a tenth of a second
        public static void Hover(GameObject root, ref float k, bool over)
        {
            if (root == null) return;
            k = Mathf.MoveTowards(k, over ? 1f : 0f, Time.unscaledDeltaTime / Ease);
            float e = k * k * (3f - 2f * k);   // eased
            float g = 1f + Grow * (1f - e);
            Vector3 s = new Vector3(g, g, 1f), d = new Vector3(e, e, 1f);
            foreach (string part in Rings)
            {
                Transform t = root.transform.Find(part);
                if (t != null && t.localScale != s) t.localScale = s;
            }
            Transform dot = root.transform.Find("dot");
            if (dot != null && dot.localScale != d) dot.localScale = d;   // grows in from nothing on hover
        }
        static readonly string[] Rings = { "ring", "shade", "holdTrack", "holdFill" };
    }
    static class Cursor
    {
        public static bool On, OffGrid, Outside, OverButton;
        public static InventoryGrid PanelGrid;   // the container the cursor is over while it is not on a slot, or null
        public static Vector2 Pos;
        static bool overTarget, inPanel;
        static Selectable hover, selected;   // a button or a toggle (the PvP switch is a toggle)
        static GameObject root;
        static Selectable[] buttons;
        static int bumperFrame = -10;
        static float buttonsAt;
        static readonly FieldInfo F_elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");
        static readonly FieldInfo F_dragGo = AccessTools.Field(typeof(InventoryGui), "m_dragGo");
        static readonly MethodInfo M_SetActiveGroup = AccessTools.Method(typeof(InventoryGui), "SetActiveGroup", new Type[] { typeof(UIGroupHandler), typeof(bool) });
        static readonly MethodInfo M_OnDropOutside = AccessTools.Method(typeof(InventoryGui), "OnDropOutside");

        public static void Stop() { Unpark(); On = OffGrid = Outside = OverButton = false; PanelGrid = null; hover = selected = null; if (root != null) root.SetActive(false); }
        public static void Pause() { if (root != null) root.SetActive(false); }

        public static bool Over(RectTransform rt) { return rt != null && rt.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rt, Pos, null); }

        // Over a panel, counting the wood frame drawn around it: the art reaches past the panel's own edges, and the
        // border of a panel must never count as "outside" (that is where an item would be dropped).
        public static bool OverPanel(RectTransform rt)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy) return false;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, Pos, null, out local)) return false;
            Rect r = rt.rect;
            const float frame = 26f;
            return local.x >= r.xMin - frame && local.x <= r.xMax + frame && local.y >= r.yMin - frame && local.y <= r.yMax + frame;
        }

        static readonly Vector3[] corners = new Vector3[4];
        static int lastGroup = -1;
        static float travel;

        // Where the focus currently is: the selected slot of whichever grid is active, or the selected control of any
        // other panel (crafting, for one). The cursor starts here, and jumps here when the panel is switched.
        static bool Focus(InventoryGui gui, out Vector2 at)
        {
            at = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            RectTransform t = null;
            InventoryGrid pg = gui.m_playerGrid, cg = gui.IsContainerOpen() ? gui.ContainerGrid : null;
            if (pg != null && pg.m_uiGroup != null && pg.m_uiGroup.IsActive) t = pg.GetGamepadSelectedElement();
            else if (cg != null && cg.m_uiGroup != null && cg.m_uiGroup.IsActive) t = cg.GetGamepadSelectedElement();
            else
            {
                int g = gui.ActiveGroup;
                UIGroupHandler grp = gui.m_uiGroups != null && g >= 0 && g < gui.m_uiGroups.Length ? gui.m_uiGroups[g] : null;
                GameObject sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                if (grp != null)
                {
                    if (sel != null && sel.transform.IsChildOf(grp.transform)) t = sel.transform as RectTransform;
                    else if (grp.m_defaultElement != null) t = grp.m_defaultElement.transform as RectTransform;
                    else t = grp.transform as RectTransform;
                }
            }
            if (t == null) return false;
            t.GetWorldCorners(corners);
            at = (corners[0] + corners[2]) * 0.5f;
            return true;
        }

        public static void Step(InventoryGui gui)
        {
            if (!Plugin.CursorEnabled.Value) { Stop(); return; }
            Vector2 stick = ZInput.GetJoyLeftStick();
            float mag = stick.magnitude;
            if (!On)
            {
                if (mag < 0.35f) return;
                On = true; OffGrid = Outside = OverButton = false; travel = 0f;
                Focus(gui, out Pos);   // start from wherever the D-pad focus was, in whichever panel that is
                lastGroup = gui.ActiveGroup;
            }
            else if (Core.RawHeld("JoyDPadLeft") || Core.RawHeld("JoyDPadRight") || Core.RawHeld("JoyDPadUp") || Core.RawHeld("JoyDPadDown"))
            {
                Stop();   // the D-pad takes over again
                return;
            }
            else
            {
                // The cursor only jumps when the panel was switched with the bumpers. The game also moves its focus
                // on its own (pressing a recipe does), and following that is what threw the cursor back to the bag.
                if (Core.RawDown("JoyTabLeft") || Core.RawDown("JoyTabRight") || Core.RawDown("JoyLBumper") || Core.RawDown("JoyRBumper")) bumperFrame = Time.frameCount;
                if (gui.ActiveGroup != lastGroup && Time.frameCount - bumperFrame <= 3)
                {
                    Focus(gui, out Pos);
                    travel = 0f;
                }
            }

            // Three speeds. Slowest over something that can be pressed, for precision. Faster across a panel. And in
            // the open space between panels it builds up to a much higher speed, so crossing the screen is quick.
            float dt = Time.unscaledDeltaTime;
            if (mag > 0.15f)
            {
                bool open = !overTarget && !inPanel;
                travel = open ? Mathf.Min(1f, travel + dt / 0.18f) : 0f;
                float baseSpeed = overTarget ? Plugin.CursorSlotSpeed.Value
                                : inPanel ? Plugin.CursorPanelSpeed.Value
                                : Mathf.Lerp(Plugin.CursorPanelSpeed.Value, Plugin.CursorTravelSpeed.Value, travel);
                float speed = baseSpeed * (Screen.height / 1080f) * Mathf.Pow(Mathf.Min(1f, mag), 1.6f);
                Pos += stick / mag * speed * dt;
                Pos.x = Mathf.Clamp(Pos.x, 4f, Screen.width - 4f); Pos.y = Mathf.Clamp(Pos.y, 4f, Screen.height - 4f);
            }
            else travel = 0f;

            // what is under the cursor: a slot first, then any ordinary button
            InventoryGrid hitGrid = null; Vector2i hitPos = default(Vector2i);
            for (int g = 0; g < 2 && hitGrid == null; g++)
            {
                InventoryGrid grid = g == 0 ? gui.m_playerGrid : (gui.IsContainerOpen() ? gui.ContainerGrid : null);
                if (grid == null || !grid.gameObject.activeInHierarchy) continue;
                System.Collections.Generic.List<InventoryElement> els = F_elements.GetValue(grid) as System.Collections.Generic.List<InventoryElement>;
                if (els == null) continue;
                // every slot counts, empty ones too (m_used only means "has an item"): an empty slot is where a carried item is placed
                foreach (InventoryElement el in els)
                    if (el != null && Over(el.transform as RectTransform)) { hitGrid = grid; hitPos = el.Position; break; }
                if (hitGrid != null) break;
                // Between two slots there is a thin strip that belongs to neither. Crossing it used to count as leaving
                // the grid, which flipped every label in the guide for a moment. The strip now belongs to the nearest slot.
                float bestGap = float.MaxValue; InventoryElement near = null;
                foreach (InventoryElement el in els)
                {
                    RectTransform rt = el != null ? el.transform as RectTransform : null;
                    if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                    rt.GetWorldCorners(corners);
                    float dx = Mathf.Max(Mathf.Max(corners[0].x - Pos.x, Pos.x - corners[2].x), 0f);
                    float dy = Mathf.Max(Mathf.Max(corners[0].y - Pos.y, Pos.y - corners[2].y), 0f);
                    float gap = Mathf.Max(dx, dy);
                    if (gap < bestGap) { bestGap = gap; near = el; }
                }
                if (near != null && bestGap <= 5f * Mathf.Max(0.01f, near.transform.lossyScale.y)) { hitGrid = grid; hitPos = near.Position; }
            }
            hover = null;
            if (hitGrid != null)
            {
                OffGrid = Outside = false;
                hitGrid.SetGamepadSelection(hitPos);
                if (hitGrid.m_uiGroup != null && !hitGrid.m_uiGroup.IsActive && M_SetActiveGroup != null) M_SetActiveGroup.Invoke(gui, new object[] { hitGrid.m_uiGroup, false });
                RectTransform el = hitGrid.GetGamepadSelectedElement();
                Selectable s = el != null ? el.GetComponent<Selectable>() : null;
                if (s != null && selected != s) { s.Select(); selected = s; }
            }
            else
            {
                OffGrid = true;
                hover = FindButton(gui);
                if (hover != null && hover != selected) { hover.Select(); selected = hover; }
                else if (hover == null) Park(gui);
            }
            inPanel = hitGrid != null || OverPanel(gui.m_player) || OverPanel(gui.m_container) || OverPanel(gui.m_crafting) || OverPanel(gui.m_info);
            // On the bag or the chest but not on a slot (the frame, the title, a tab on the side): still "in" that container.
            PanelGrid = null;
            if (hitGrid == null && hover == null)
            {
                if (gui.IsContainerOpen() && OverPanel(gui.m_container)) PanelGrid = gui.ContainerGrid;
                else if (OverPanel(gui.m_player)) PanelGrid = gui.m_playerGrid;
            }
            Outside = OffGrid && hover == null && !inPanel;
            lastGroup = gui.ActiveGroup;
            OverButton = hover != null;
            overTarget = hitGrid != null || hover != null;
            // The hover look: over an item or a button, and while carrying an item over any slot (somewhere to put it).
            // Not over an empty slot: every slot counted at first, so in the bag the cursor was always "on" and never
            // seemed to change.
            bool carrying = F_dragGo.GetValue(gui) as GameObject != null;
            onSomething = hover != null || (hitGrid != null && (carrying || (hitGrid.GetInventory() != null && hitGrid.GetInventory().GetItemAt(hitPos.x, hitPos.y) != null)));
            Draw(gui);
        }

        static Selectable FindButton(InventoryGui gui)
        {
            if (buttons == null || Time.unscaledTime > buttonsAt)
            {
                buttons = gui.GetComponentsInChildren<Selectable>(false);
                buttonsAt = Time.unscaledTime + 0.5f;
            }
            Selectable best = null; float bestArea = float.MaxValue;
            foreach (Selectable b in buttons)
            {
                if (b == null || !(b is Button || b is Toggle) || !b.isActiveAndEnabled || !b.IsInteractable()) continue;   // IsInteractable: also off when a CanvasGroup above turns it off (the chest buttons on the controller)
                if (b.name.StartsWith("cp_") || b.GetComponentInParent<InventoryElement>() != null) continue;
                // The game's drop area is an invisible button behind the whole inventory. It is not something to point
                // at: dropping is the cursor's own job, and only outside every panel.
                if (b == gui.m_dropButton) continue;
                // A button counts over its own box and over the picture it draws. On the big round buttons at the top
                // (skills, trophies...) the picture is a good deal larger than the box, and only the box was counted.
                // A button counts only over its own box. (For a while its picture counted too, but on the big round
                // buttons at the top the pictures are larger than the buttons and overlap their neighbours, so the
                // hover jumped between them.)
                RectTransform rt = b.transform as RectTransform;
                if (!Over(rt)) continue;
                float area = Mathf.Abs(rt.rect.width * rt.rect.height);
                if (area < bestArea) { best = b; bestArea = area; }   // the smallest one under the cursor is the one meant
            }
            return best;
        }

        public static bool OverThis(Selectable s) { return s != null && hover == s; }

        // Over nothing: nothing should look selected, and A must not press a button the cursor has already left.
        // Simply clearing the selection does not hold: the game's panel sees "nothing selected" and at once selects
        // its first button again, so a button lit up on its own and the highlight seemed to jump about. So the
        // selection is parked on an invisible, do-nothing control inside the panel in use. The panel is satisfied that
        // something of its own is selected, and nothing lights up.
        static GameObject parked;
        static void Park(InventoryGui gui)
        {
            selected = null;
            if (EventSystem.current == null) return;
            if (parked == null)
            {
                parked = new GameObject("cp_nothing", typeof(RectTransform));
                Selectable s = parked.AddComponent<Selectable>();
                s.transition = Selectable.Transition.None;
                Navigation none = new Navigation(); none.mode = Navigation.Mode.None; s.navigation = none;
                ((RectTransform)parked.transform).sizeDelta = Vector2.zero;
            }
            int g = gui.ActiveGroup;
            Transform home = gui.m_uiGroups != null && g >= 0 && g < gui.m_uiGroups.Length && gui.m_uiGroups[g] != null ? gui.m_uiGroups[g].transform : gui.transform;
            if (parked.transform.parent != home) { parked.transform.SetParent(home, false); parked.layer = home.gameObject.layer; }
            if (EventSystem.current.currentSelectedGameObject != parked) EventSystem.current.SetSelectedGameObject(parked);
        }
        static void Unpark()
        {
            // give the selection back, so the D-pad starts from one of the panel's own buttons again
            if (parked != null && EventSystem.current != null && EventSystem.current.currentSelectedGameObject == parked) EventSystem.current.SetSelectedGameObject(null);
        }

        public static void Click(InventoryGui gui)
        {
            if (hover == null) return;
            UIGroupHandler group = hover.GetComponentInParent<UIGroupHandler>();
            Button b = hover as Button; Toggle tg = hover as Toggle;
            if (b != null) b.onClick.Invoke();
            else if (tg != null) tg.isOn = !tg.isOn;
            // The game's own focus goes to the panel that was just pressed in, so the cursor (and the D-pad) carry on
            // from there instead of from the bag.
            if (group != null && !group.IsActive && M_SetActiveGroup != null) M_SetActiveGroup.Invoke(gui, new object[] { group, false });
            lastGroup = gui.ActiveGroup;
        }

        public static void Drop(InventoryGui gui) { if (M_OnDropOutside != null) M_OnDropOutside.Invoke(gui, null); }

        // the carried item rides along with the cursor
        public static void FollowDrag(InventoryGui gui)
        {
            if (!On) return;
            GameObject drag = F_dragGo.GetValue(gui) as GameObject;
            if (drag == null) return;
            // The carried icon is placed by where its picture actually is, not by the object that holds it (the game
            // builds that object with the picture offset to one side). The picture sits on the cursor, nudged just
            // enough down and right that the cursor's ring stays visible on its corner.
            Transform icon = drag.transform.Find("icon");
            Vector3 offset = Vector3.zero;
            if (icon != null)
            {
                ((RectTransform)icon).GetWorldCorners(corners);
                offset = drag.transform.position - (corners[0] + corners[2]) * 0.5f;
            }
            float s = Screen.height / 1080f;
            drag.transform.position = new Vector3(Pos.x + 14f * s, Pos.y - 14f * s, drag.transform.position.z) + new Vector3(offset.x, offset.y, 0f);
        }

        static Image curRing, curDot, curTrack, curFill;
        // A hold in progress (0 to 1) shown on the cursor in place of its ring and dot. 0 shows the normal cursor.
        public static void ShowHold(float r)
        {
            if (root == null || curFill == null) return;
            bool holding = r > 0f;
            if (curRing.enabled == holding) { curRing.enabled = curDot.enabled = !holding; curTrack.enabled = curFill.enabled = holding; }
            curFill.fillAmount = r;
        }

        static void Draw(InventoryGui gui)
        {
            if (root == null)
            {
                Vector2 mid = new Vector2(0.5f, 0.5f);
                root = Hints.New("ControllerPro_Cursor", gui.transform);
                Hints.Place(root, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
                Image shade = Hints.Img(Hints.New("shade", root.transform), Hints.Ring(), new Color(0f, 0f, 0f, 0.7f));
                Hints.Place(shade.gameObject, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
                curRing = Hints.Img(Hints.New("ring", root.transform), Hints.Ring(), Color.white);
                Hints.Place(curRing.gameObject, mid, mid, mid, Vector2.zero, new Vector2(26f, 26f));
                curDot = Hints.Img(Hints.New("dot", root.transform), Hints.Disc(), Color.white);
                Hints.Place(curDot.gameObject, mid, mid, mid, Vector2.zero, new Vector2(5f, 5f));
                // while a hold fills, the cursor itself becomes the hold ring: a faint track and a clockwise fill
                curTrack = Hints.Img(Hints.New("holdTrack", root.transform), Hints.Ring(), new Color(1f, 1f, 1f, 0.25f));
                Hints.Place(curTrack.gameObject, mid, mid, mid, Vector2.zero, new Vector2(26f, 26f));
                curFill = Hints.Img(Hints.New("holdFill", root.transform), Hints.Ring(), Color.white);
                Hints.Place(curFill.gameObject, mid, mid, mid, Vector2.zero, new Vector2(26f, 26f));
                curFill.type = Image.Type.Filled; curFill.fillMethod = Image.FillMethod.Radial360;
                curFill.fillOrigin = (int)Image.Origin360.Top; curFill.fillClockwise = true;
                curTrack.enabled = curFill.enabled = false;
            }
            root.SetActive(true);
            root.transform.SetAsLastSibling();
            root.transform.position = new Vector3(Pos.x, Pos.y, root.transform.position.z);
            CursorLook.Hover(root, ref hoverK, onSomething);
        }
        static float hoverK;
        static bool onSomething;
    }

    // ---------- hotbar buttons and item levels in the inventory ----------
    // On the controller, each bag item on the Controller Hotbar shows its button (a D-pad direction, or a face button for
    // the LT set) in the slot's top left corner, where the game writes its number keys 1 to 8 on the first row. Those
    // numbers mean nothing on a controller, so they are hidden. In every inventory grid (bag and chest) an item's level,
    // which the game writes as a number in the top right corner, is drawn as a column of small diamonds in the number's
    // own orange, one per level. Keyboard and mouse get the game's numbers back.
    [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
    static class Patch_GridMarkers
    {
        static readonly FieldInfo F_elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");
        class Slot { public TMP_Text number, mark; public bool numberOn, looked; public float inset = -1f; public RectTransform levels; public readonly List<Image> diamonds = new List<Image>(); }
        const float MarkScale = 0.8f, Diamond = 5f, DiamondPitch = 8f;
        static readonly Vector3[] ic = new Vector3[4];
        // how far in from the corners the corner marks sit: a little closer than the stack count is to its corner
        static float Inset(InventoryElement el)
        {
            RectTransform slot = el.transform as RectTransform;
            TMP_Text amount = el.m_amount;
            if (slot == null || amount == null) return 3f;
            amount.ForceMeshUpdate();
            Bounds tb = amount.textBounds;
            if (tb.size.x <= 0f) { amount.rectTransform.GetWorldCorners(ic); }
            else { ic[2] = amount.transform.TransformPoint(tb.max); ic[0] = amount.transform.TransformPoint(tb.min); }
            Vector3 br = slot.InverseTransformPoint(new Vector3(ic[2].x, ic[0].y, 0f));
            float gap = Mathf.Max(slot.rect.xMax - br.x, br.y - slot.rect.yMin);
            return gap >= 2f && gap <= 12f ? Mathf.Max(2f, gap - 2f) : 3f;
        }
        static readonly Dictionary<InventoryElement, Slot> slots = new Dictionary<InventoryElement, Slot>();

        static void Postfix(InventoryGrid __instance)
        {
            long pt = Perf.Now();
            try
            {
                InventoryGui gui = InventoryGui.instance;
                if (gui == null) return;
                bool bag = __instance == gui.m_playerGrid;
                List<InventoryElement> els = F_elements.GetValue(__instance) as List<InventoryElement>;
                Inventory inv = __instance.GetInventory();
                if (els == null || inv == null) return;
                bool pad = Plugin.Enabled.Value && ZInput.IsGamepadActive();
                bool favs = bag && pad && Plugin.FavEnabled.Value;
                bool marks = favs && Plugin.FavMarkers.Value;
                foreach (InventoryElement el in els)
                {
                    if (el == null) continue;
                    Slot s;
                    if (!slots.TryGetValue(el, out s) || !s.looked)
                    {
                        s = new Slot(); s.looked = true;
                        Transform b = el.transform.Find("binding");
                        s.number = b != null ? b.GetComponent<TMP_Text>() : null;
                        s.numberOn = s.number != null && s.number.enabled;   // the game turns it on for the first row only, once
                        slots[el] = s;
                    }
                    ItemDrop.ItemData it = el.m_used ? inv.GetItemAt(el.Position.x, el.Position.y) : null;
                    // The level diamonds are archived (Rob, 2026-11-01): the game's own level number is back. Given no item,
                    // Levels puts any diamonds away and leaves the number to the game. Pass `pad ? it : null` to bring them back.
                    Levels(el, s, null);
                    if (!bag) continue;
                    Deposit.Grey(el, it);   // greyed while a mannequin, stand or altar is open and this cannot go on it

                    // the game's number: off on the controller, back as the game had it otherwise
                    if (s.number != null) { bool want = s.numberOn && !favs; if (s.number.enabled != want) s.number.enabled = want; }
                    /* the top row: every slot shows its button, with or without an item in it (0.88.5; before, only slots holding
                       an item did) */
                    string glyph = marks && el.Position.y == 0 ? Favorites.ColumnGlyph(el.Position.x) : null;
                    bool fav = !string.IsNullOrEmpty(glyph);
                    if (s.mark == null)
                    {
                        if (!fav) continue;   // only made when first needed
                        TMP_Text like = s.number != null ? s.number : (gui.m_weight != null ? gui.m_weight : gui.m_containerName);
                        GameObject go = Hints.New("ControllerPro_Fav", el.transform);
                        // a little smaller than the number it replaces
                        s.mark = Hints.Text(go, like, like.fontSize * MarkScale, Color.white, TextAlignmentOptions.TopLeft);
                        s.mark.raycastTarget = false;
                        Vector2 tl = new Vector2(0f, 1f);
                        float inset = SlotInset(el, s);
                        Hints.Place(go, tl, tl, tl, new Vector2(inset, -inset), new Vector2(24f, 18f));
                    }
                    if (s.mark.gameObject.activeSelf != fav) s.mark.gameObject.SetActive(fav);
                    if (fav && s.mark.text != glyph) s.mark.text = glyph;
                }
            }
            catch (Exception e) { Core.Fail(e); }
            finally { Perf.Lap("bag marks", pt); }
        }
        static float SlotInset(InventoryElement el, Slot s) { if (s.inset < 0f) s.inset = Inset(el); return s.inset; }

        // The level as diamonds, top right, one under another: shown where the game shows its number (items that have
        // levels), and the number put away. Given no item (keyboard and mouse), the diamonds go and the game's number,
        // which the game sets again on every update, is left to it.
        static void Levels(InventoryElement el, Slot s, ItemDrop.ItemData it)
        {
            TMP_Text q = el.m_quality;
            int n = it != null && q != null && q.enabled ? Mathf.Clamp(it.m_quality, 0, 10) : 0;
            if (n > 0)
            {
                q.enabled = false;
                if (s.levels == null)
                {
                    Vector2 tr = new Vector2(1f, 1f);
                    float inset = SlotInset(el, s);
                    s.levels = Hints.Place(Hints.New("ControllerPro_Level", el.transform), tr, tr, tr, new Vector2(-inset, -inset), new Vector2(Diamond * 1.42f, 80f));
                }
                while (s.diamonds.Count < n)
                {
                    Image d = Hints.Img(Hints.New("d" + s.diamonds.Count, s.levels), Hints.Solid(), Color.white);
                    d.raycastTarget = false;
                    Vector2 top = new Vector2(0.5f, 1f), mid = new Vector2(0.5f, 0.5f);
                    // a square turned on its corner; its middle half a diagonal down from the top of the column
                    Hints.Place(d.gameObject, top, top, mid, new Vector2(0f, -(Diamond * 0.71f + s.diamonds.Count * DiamondPitch)), new Vector2(Diamond, Diamond));
                    d.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
                    s.diamonds.Add(d);
                }
            }
            if (s.levels == null) return;
            if (s.levels.gameObject.activeSelf != (n > 0)) s.levels.gameObject.SetActive(n > 0);
            for (int i = 0; i < s.diamonds.Count; i++)
            {
                bool on = i < n;
                if (s.diamonds[i].gameObject.activeSelf != on) s.diamonds[i].gameObject.SetActive(on);
                if (on && q != null) s.diamonds[i].color = q.color;   // the number's own orange
            }
        }
    }

    // ---------- the game's own gameplay hints (Attack, Block, Dodge...) ----------
    // On the controller these are restacked vertically under the minimap, so they no longer run along the bottom of the
    // screen into the favorites. This is the one place hints are stacked. Nothing is rebuilt: the game's own hint row is
    // moved, and it goes back to where it was the moment keyboard or mouse takes over.
    static class HudHints
    {
        public const float StackRow = 26f;   // the height of one row in any stacked guide
        class Saved { public RectTransform rt; public Vector2 aMin, aMax, pivot, pos, size; public TMP_Text text; public bool wrap; public TextAlignmentOptions align; }
        class Block
        {
            public RectTransform box; public HorizontalLayoutGroup row; public ContentSizeFitter fitter;
            public bool applied, rowWasOn, fitterWasOn;
            public System.Collections.Generic.List<Saved> saved = new System.Collections.Generic.List<Saved>();
            // the remapped buttons: the game's own Dodge hint, and a Sneak hint of the mod's made in its image
            public TMP_Text dodge, sneak;
            public string dodgeGame, dodgeOurs;
            public bool logged;
        }

        // A hint of the mod's own, copied from one of the game's so it has the same font, size and look.
        static TMP_Text Copy(TMP_Text like, string name, int index)
        {
            GameObject go = UnityEngine.Object.Instantiate(like.gameObject, like.transform.parent);
            go.name = name;
            go.transform.SetSiblingIndex(index);
            // the copy must not be re-translated by the game back into the text it was copied from
            foreach (MonoBehaviour m in go.GetComponents<MonoBehaviour>())
                if (m != null && !(m is TMP_Text) && m.GetType().Name.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0) UnityEngine.Object.Destroy(m);
            return go.GetComponent<TMP_Text>();
        }

        static void Set(TMP_Text t, bool on, string text)
        {
            if (t == null) return;
            if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
            if (on && t.text != text) t.text = text;
        }

        // The hints follow the remapped buttons: Dodge shows B on its own, and Sneak and the Forsaken power get a hint each.
        static void Remapped(Block b)
        {
            if (b.dodge == null) return;
            bool pad = Plugin.Enabled.Value && ZInput.IsGamepadActive();
            bool dodge = pad && Remap.DodgeLayout();
            string cur = b.dodge.text;
            if (cur != b.dodgeOurs) b.dodgeGame = cur;   // the game wrote it: keep its version to go back to
            if (dodge && !string.IsNullOrEmpty(b.dodgeGame))
            {
                int at = b.dodgeGame.IndexOf("<mspace", StringComparison.Ordinal), sprite = b.dodgeGame.IndexOf("<sprite", StringComparison.Ordinal);
                if (at < 0 || (sprite >= 0 && sprite < at)) at = sprite;
                string label = (at > 0 ? b.dodgeGame.Substring(0, at) : "Dodge").Trim();
                b.dodgeOurs = label + "  " + Hints.Glyph("JoyDodge");
                if (cur != b.dodgeOurs) b.dodge.text = b.dodgeOurs;
                if (!b.logged) { b.logged = true; Plugin.Log.LogInfo("Dodge hint: the game's text is '" + b.dodgeGame + "', the PRO text is '" + b.dodgeOurs + "'"); }
            }
            else if (b.dodgeOurs != null)
            {
                if (cur == b.dodgeOurs && b.dodgeGame != null) b.dodge.text = b.dodgeGame;
                b.dodgeOurs = null;
            }
            bool shown = b.dodge.gameObject.activeSelf;
            Set(b.sneak, dodge && shown, "Hold \u00b7 Sneak  " + Hints.Glyph("JoyDodge"));
            // the Forsaken power (LB + RB) deliberately has no hint
        }

        // Where the drawing in a hint really ends on the right: the last visible letter or button glyph. The text's own
        // box cannot be trusted for this, because a glyph can carry empty space after it, and different glyphs carry
        // different amounts. That is what left some rows ending short of the others.
        public static bool InkRight(TMP_Text t, out float right)
        {
            right = float.MinValue;
            TMP_TextInfo info = t.textInfo;
            if (info == null || info.characterInfo == null) return false;
            int n = Mathf.Min(info.characterCount, info.characterInfo.Length);
            for (int i = 0; i < n; i++)
            {
                TMP_CharacterInfo ch = info.characterInfo[i];
                if (ch.isVisible && ch.topRight.x > right) right = ch.topRight.x;
            }
            return right > float.MinValue;
        }
        static readonly System.Collections.Generic.Dictionary<GameObject, Block> blocks = new System.Collections.Generic.Dictionary<GameObject, Block>();
        static readonly Vector3[] corners = new Vector3[4];

        static Saved Keep(RectTransform rt)
        {
            Saved s = new Saved(); s.rt = rt; s.aMin = rt.anchorMin; s.aMax = rt.anchorMax; s.pivot = rt.pivot; s.pos = rt.anchoredPosition; s.size = rt.sizeDelta;
            s.text = rt.GetComponent<TMP_Text>();
            if (s.text != null) { s.wrap = s.text.enableWordWrapping; s.align = s.text.alignment; }
            return s;
        }

        // Every hint should end with the button to press. One of the game's hints ("Alternative placement mode") puts a
        // word after its buttons ("On" / "Off"). This moves any such trailing word in front of the buttons.
        static readonly System.Collections.Generic.Dictionary<TMP_Text, string> rewritten = new System.Collections.Generic.Dictionary<TMP_Text, string>();
        static void ButtonsLast(TMP_Text t)
        {
            string text = t.text, done;
            if (string.IsNullOrEmpty(text)) return;
            if (rewritten.TryGetValue(t, out done) && done == text) return;   // already in the right order
            int lastSprite = text.LastIndexOf("<sprite", StringComparison.Ordinal);
            int close = lastSprite >= 0 ? text.IndexOf('>', lastSprite) : -1;
            if (close < 0) { rewritten[t] = text; return; }
            string tail = text.Substring(close + 1);
            string word = System.Text.RegularExpressions.Regex.Replace(tail, "<[^>]*>", "").Trim();
            if (word.Length == 0) { rewritten[t] = text; return; }
            int block = text.IndexOf("<mspace", StringComparison.Ordinal);
            if (block < 0) block = text.IndexOf("<sprite", StringComparison.Ordinal);
            string tags = System.Text.RegularExpressions.Regex.Replace(tail, "[^<]*(<[^>]*>)?", "$1");   // keep any closing tags that followed the buttons
            string result = text.Substring(0, block).TrimEnd() + " " + word + "  " + text.Substring(block, close + 1 - block) + tags;
            t.text = result;
            rewritten[t] = result;
        }

        // ----- the radial's hints, beside the wheel -----
        // To the right of the wheel, a little clear of it, centred on it, one hint per line, left-aligned with its buttons
        // first. The wheel grows round the picked item, so the stack is placed by the widest the wheel has been since it
        // opened: it never touches it, and does not jump about as the pick moves.
        const float WheelGap = 28f;
        static float wheelRight = float.MinValue, wheelMid, wheelK; static bool wheelLogged; static Transform wheelCentre;
        static bool WheelEdge(out float right, out float midY)
        {
            right = 0f; midY = 0f;
            Valheim.UI.RadialBase r = Hud.instance != null ? Hud.instance.m_radialMenu : null;
            if (r == null || !r.Active) { wheelRight = float.MinValue; return false; }
            // Worked out once per opening and kept until it closes (0.88.1): the wheel's centre, and its outer edge at its
            // largest (the game's element distance, an element at its picked size, and the picked element's nudge out), so
            // the guide never moves as the pick or the number of items changes. (0.87.9 followed the drawn items.)
            if (wheelRight == float.MinValue)
            {
                Valheim.UI.RadialMenuElement any = null;
                foreach (Valheim.UI.RadialMenuElement e in r.GetComponentsInChildren<Valheim.UI.RadialMenuElement>(false)) { any = e; break; }
                if (any == null) return false;
                Transform p = any.transform.parent != null ? any.transform.parent : r.transform;
                Valheim.UI.RadialDataSO so = Valheim.UI.RadialData.SO;
                float size = ((RectTransform)any.transform).rect.width;
                float reach = so != null ? so.ElementsDistance + size * 0.5f * Mathf.Max(1f, so.ElementScaleFactor) + (so.NudgeSelectedElement ? so.NudgeDistance : 0f) : size * 2f;
                /* one place for every wheel (0.88.2): at least half the game's full wheel width out from the centre, so the bigger
                   wheels (Emotes) and the small ones (Consumables) put the guide in the same spot, clear of the largest */
                if (so != null && so.RadialReferenceWidth > 0f && so.RadialReferenceWidth * 0.5f <= reach * 3f) reach = Mathf.Max(reach, so.RadialReferenceWidth * 0.5f);
                /* and the widest any wheel has been drawn, remembered across sessions: after the Emotes wheel has been opened once,
                   every wheel puts the guide in the same place */
                reach = Mathf.Max(reach, Plugin.WheelReachSeen.Value);
                wheelCentre = p; wheelK = p.lossyScale.x;
                reach += Mathf.Max(0f, Plugin.WheelHintsExtra.Value);
                float k = p.lossyScale.x;
                wheelRight = p.position.x + reach * k; wheelMid = p.position.y;
                Plugin.Log.LogInfo("Wheel hints: reference width " + (so != null ? so.RadialReferenceWidth : 0f) + ", element distance " + (so != null ? so.ElementsDistance : 0f) + "; wheel centre " + p.position + " (" + p.name + "), reach " + reach + " x " + k + ", item size " + size);
            }
            // measuring the wheel as drawn: the furthest an item reaches from the centre, kept if it is the widest yet (saved only
            // when it grows by more than a little, so the file is not written every frame)
            if (wheelCentre != null && wheelK > 0f)
            {
                float far = 0f;
                foreach (Valheim.UI.RadialMenuElement e in r.GetComponentsInChildren<Valheim.UI.RadialMenuElement>(false))
                {
                    CanvasGroup g = e.GetComponent<CanvasGroup>(); if (g != null && g.alpha < 0.05f) continue;
                    ((RectTransform)e.transform).GetWorldCorners(corners);
                    for (int i = 0; i < 4; i++) far = Mathf.Max(far, corners[i].x - wheelCentre.position.x);
                }
                float farUnits = far / wheelK;
                if (farUnits > Plugin.WheelReachSeen.Value + 4f) { Plugin.WheelReachSeen.Value = Mathf.Ceil(farUnits); Plugin.Log.LogInfo("Wheel hints: widest wheel now " + Plugin.WheelReachSeen.Value); }
            }
            right = wheelRight; midY = wheelMid;
            return true;
        }        static readonly System.Collections.Generic.Dictionary<TMP_Text, string> firsted = new System.Collections.Generic.Dictionary<TMP_Text, string>();
        // the buttons first, then the words ("Close  [B]" becomes "[B]  Close")
        static void ButtonsFirst(TMP_Text t)
        {
            string text = t.text, done;
            if (string.IsNullOrEmpty(text)) return;
            if (firsted.TryGetValue(t, out done) && done == text) return;
            int a = text.IndexOf("<mspace", StringComparison.Ordinal);
            if (a < 0) a = text.IndexOf("<sprite", StringComparison.Ordinal);
            int endTag = Math.Max(text.LastIndexOf("</mspace>", StringComparison.Ordinal), -1);
            int e = endTag >= 0 ? endTag + "</mspace>".Length : -1;
            if (e < 0) { int ls = text.LastIndexOf("<sprite", StringComparison.Ordinal); e = ls >= 0 ? text.IndexOf('>', ls) + 1 : -1; }
            if (a <= 0 || e <= a) { firsted[t] = text; return; }
            string words = (text.Substring(0, a).Trim() + " " + text.Substring(e).Trim()).Trim();
            string result = text.Substring(a, e - a) + "  " + words;
            t.text = result; firsted[t] = result;
        }
        static void BesideWheel(Block b)
        {
            float right, mid;
            if (!WheelEdge(out right, out mid)) return;
            float scale = b.box.lossyScale.y;
            float rowH = StackRow, spacing = Mathf.Max(0f, Plugin.HudHintsSpacing.Value), total = 0f, widest = 0f;
            int n = 0;
            foreach (Transform c in b.box)
            {
                if (!c.gameObject.activeSelf) continue;
                TMP_Text t = c.GetComponent<TMP_Text>();
                if (t == null) continue;
                ButtonsFirst(t);
                t.enableWordWrapping = false;
                t.alignment = TextAlignmentOptions.Left;
                float th = Mathf.Max(t.GetPreferredValues().y, t.textBounds.size.y);
                if (th <= StackRow * 1.6f) rowH = Mathf.Max(rowH, th);
            }
            foreach (Transform c in b.box)
            {
                if (!c.gameObject.activeSelf) continue;
                RectTransform rt = (RectTransform)c; TMP_Text t = rt.GetComponent<TMP_Text>();
                float h = t != null ? Mathf.Max(rowH, Mathf.Max(t.GetPreferredValues().y, t.textBounds.size.y)) : Mathf.Max(rowH, rt.rect.height);
                total += h + (n > 0 ? spacing : 0f); n++;
                if (t != null) widest = Mathf.Max(widest, t.GetPreferredValues().x);
            }
            Vector2 tl = new Vector2(0f, 1f);
            b.box.anchorMin = b.box.anchorMax = new Vector2(0.5f, 0.5f);
            /* its top pinned two rows above the wheel's middle, so a hint coming or going (a picked item that can be dropped,
               say) adds or takes a line at the bottom and moves nothing else */
            b.box.pivot = new Vector2(0f, 1f);
            b.box.sizeDelta = new Vector2(widest, total);
            b.box.position = new Vector3(right + WheelGap * scale, mid + 2f * (StackRow + spacing) * scale, b.box.position.z);
            float y = 0f;
            foreach (Transform c in b.box)
            {
                if (!c.gameObject.activeSelf) continue;
                RectTransform rt = (RectTransform)c; TMP_Text t = rt.GetComponent<TMP_Text>();
                rt.anchorMin = rt.anchorMax = rt.pivot = tl;
                float h = t != null ? Mathf.Max(rowH, Mathf.Max(t.GetPreferredValues().y, t.textBounds.size.y)) : Mathf.Max(rowH, rt.rect.height);
                if (t != null) rt.sizeDelta = new Vector2(t.GetPreferredValues().x, h);
                rt.anchoredPosition = new Vector2(0f, -y);
                y += h + spacing;
            }
            if (!wheelLogged) { wheelLogged = true; Plugin.Log.LogInfo("Wheel hints: " + n + " beside the wheel, its right edge at " + right + ", middle at " + mid); }
        }

        // Each hint group holds one row per input: a Gamepad row and a Keyboard row (until 0.78.5 only the first, the
        // controller's, was restacked, so on keyboard and mouse its row stayed where the game puts it, over a moved minimap).
        // Every row is restacked on its own.
        static readonly System.Collections.Generic.Dictionary<GameObject, List<GameObject>> rowsOf = new System.Collections.Generic.Dictionary<GameObject, List<GameObject>>();
        public static void ApplyAll(GameObject root, bool combat) { ApplyAll(root, combat, false); }
        public static void ApplyAll(GameObject root, bool combat, bool wheel)
        {
            if (root == null) return;
            List<GameObject> rows;
            if (!rowsOf.TryGetValue(root, out rows))
            {
                rows = new List<GameObject>();
                foreach (HorizontalLayoutGroup h in root.GetComponentsInChildren<HorizontalLayoutGroup>(true))
                {
                    if (h.transform.childCount < 2) continue;
                    bool inner = false;
                    foreach (GameObject r in rows) if (h.transform.IsChildOf(r.transform)) inner = true;
                    if (!inner) rows.Add(h.gameObject);
                }
                if (rows.Count == 0) rows.Add(root);
                rowsOf[root] = rows;
            }
            foreach (GameObject r in rows) { if (r == null) { rowsOf.Remove(root); return; } Apply(r, combat, wheel); }
        }

        public static void Apply(GameObject root) { Apply(root, root != null && root.name.IndexOf("Combat", StringComparison.OrdinalIgnoreCase) >= 0); }
        public static void Apply(GameObject root, bool combat) { Apply(root, combat, false); }
        public static void Apply(GameObject root, bool combat, bool wheel)
        {
            if (root == null) return;
            Block b;
            if (!blocks.TryGetValue(root, out b))
            {
                // the row of hints is the outermost horizontal row with at least two entries; failing that, the root itself
                b = new Block();
                foreach (HorizontalLayoutGroup h in root.GetComponentsInChildren<HorizontalLayoutGroup>(true))
                    if (h.transform.childCount >= 2) { b.row = h; break; }
                b.box = (RectTransform)(b.row != null ? b.row.transform : root.transform);
                b.fitter = b.box.GetComponent<ContentSizeFitter>();
                blocks[root] = b;
                if (combat)
                {
                    foreach (Transform c in b.box)
                        if (c.name.IndexOf("Dodge", StringComparison.OrdinalIgnoreCase) >= 0) { b.dodge = c.GetComponent<TMP_Text>(); break; }
                    if (b.dodge != null)
                    {
                        int at = b.dodge.transform.GetSiblingIndex();
                        b.sneak = Copy(b.dodge, "Text - ControllerPro Sneak", at + 1);
                    }
                }
                System.Text.StringBuilder sb = new System.Text.StringBuilder("HUD hints '" + root.name + "': row " + (b.row != null ? b.row.name : "none, using the root") + ", entries:");
                foreach (Transform c in b.box) sb.Append(" [" + c.name + (c.gameObject.activeSelf ? "" : " off") + "]");
                Plugin.Log.LogInfo(sb.ToString());
            }
            if (b.box == null) { blocks.Remove(root); return; }
            Remapped(b);

            Minimap map = Minimap.instance;
            // measured against the map picture itself, not its container, which reaches a little further right than the map does
            RectTransform small = map != null && map.m_mapImageSmall != null ? map.m_mapImageSmall.rectTransform
                                : map != null && map.m_smallRoot != null ? map.m_smallRoot.transform as RectTransform : null;
            // stacked on the controller (every preset), and on keyboard and mouse with the preset applied to them (Bottom HUD
            // and Action RPG; the Vanilla preset keeps the game's own row there)
            bool want = Plugin.Enabled.Value && Plugin.StackHudHints.Value && (ZInput.IsGamepadActive() || Plugin.KbmPreset) && small != null && small.gameObject.activeInHierarchy;
            /* the radial's hints: on the controller, while the wheel is open, whatever the minimap does */
            if (wheel) { want = Plugin.Enabled.Value && Plugin.StackHudHints.Value && ZInput.IsGamepadActive() && Hud.InRadial(); if (!Hud.InRadial()) wheelRight = float.MinValue; }
            if (!want)
            {
                if (!b.applied) return;
                foreach (Saved s in b.saved)
                    if (s.rt != null)
                    {
                        s.rt.anchorMin = s.aMin; s.rt.anchorMax = s.aMax; s.rt.pivot = s.pivot; s.rt.anchoredPosition = s.pos; s.rt.sizeDelta = s.size;
                        if (s.text != null) { s.text.enableWordWrapping = s.wrap; s.text.alignment = s.align; }
                    }
                if (b.row != null) b.row.enabled = b.rowWasOn;
                if (b.fitter != null) b.fitter.enabled = b.fitterWasOn;
                LayoutRebuilder.MarkLayoutForRebuild(b.box);
                b.applied = false;
                return;
            }
            if (!root.activeInHierarchy) return;
            if (!b.applied)
            {
                b.saved.Clear();
                b.saved.Add(Keep(b.box));
                foreach (Transform c in b.box) b.saved.Add(Keep((RectTransform)c));
                if (b.row != null) { b.rowWasOn = b.row.enabled; b.row.enabled = false; }
                if (b.fitter != null) { b.fitterWasOn = b.fitter.enabled; b.fitter.enabled = false; }
                b.applied = true;
            }

            if (wheel) { BesideWheel(b); return; }
            // The stack hangs under the minimap's bottom-right corner. With the minimap moved to the bottom right it stands
            // on the map's top-right corner instead and grows upward, so the hint that was first (nearest the map) is
            // still the nearest: the order reads upside down, as asked.
            small.GetWorldCorners(corners);
            // upward only when the map really is at the bottom: if it could not be moved, the hints stay under it
            bool up = Plugin.MinimapBottom && MapPlace.Moved && corners[0].y < Screen.height * 0.5f;
            Vector2 topRight = up ? new Vector2(1f, 0f) : new Vector2(1f, 1f);
            float scale = b.box.lossyScale.y;
            b.box.pivot = topRight;
            // with the map switched off the stack takes its place: from the map's bottom edge upward, or its top edge down
            float gap = Plugin.HudHintsGap.Value * scale;
            if (MapPlace.Hidden) b.box.position = up ? new Vector3(corners[3].x, corners[3].y, b.box.position.z) : new Vector3(corners[2].x, corners[2].y, b.box.position.z);
            else b.box.position = up ? new Vector3(corners[2].x, corners[2].y + gap, b.box.position.z)
                                     : new Vector3(corners[3].x, corners[3].y - gap, b.box.position.z);
            // Each hint was sized by the horizontal row it used to sit in, which left boxes of different widths and heights
            // and made long hints wrap. Give each one a single line, flush right. Every row gets the same height, taken
            // from the tallest thing drawn in any of them (the trigger and stick glyphs are taller than the text), so the
            // space between rows is the same all the way down.
            float rowH = StackRow;   // never shorter than a row of the crafting guide, so the two stacks share one rhythm
            foreach (Transform c in b.box)
            {
                if (!c.gameObject.activeSelf) continue;
                TMP_Text t = c.GetComponent<TMP_Text>();
                if (t == null) continue;   // not a line of text: it keeps its own height below, and does not set everyone's
                ButtonsLast(t);
                t.enableWordWrapping = false;
                t.alignment = TextAlignmentOptions.Right;
                // only single-line hints set the shared row height: one taller entry (the hammer's hints have some) made
                // every row that tall and spread the whole stack apart (until 0.78.7)
                float th = Mathf.Max(t.GetPreferredValues().y, t.textBounds.size.y);
                if (th <= StackRow * 1.6f) rowH = Mathf.Max(rowH, th);
            }
            // The gap is in the same units as the crafting guide's, not in screen pixels. Measured in pixels it came out
            // about a third smaller than the crafting guide's on this screen, which is why this stack looked tighter.
            float spacing = Mathf.Max(0f, Plugin.HudHintsSpacing.Value);
            float y = 0f;
            foreach (Transform c in b.box)
            {
                if (!c.gameObject.activeSelf) continue;
                RectTransform rt = (RectTransform)c;
                rt.anchorMin = topRight; rt.anchorMax = topRight; rt.pivot = topRight;
                TMP_Text t = rt.GetComponent<TMP_Text>();
                float dx = 0f, h = rowH;
                if (t == null) h = Mathf.Max(rowH, rt.rect.height);   // a piece that is not text keeps its own height
                else
                {
                    h = Mathf.Max(rowH, Mathf.Max(t.GetPreferredValues().y, t.textBounds.size.y));   // a taller hint takes its own room only
                    rt.sizeDelta = new Vector2(t.GetPreferredValues().x, h);
                    // Line the rows up by what is actually drawn, not by their text boxes: glyphs of different shapes
                    // end at slightly different points inside the box. This nudges each row so its last glyph ends on
                    // the same edge.
                    float inkRight;
                    if (InkRight(t, out inkRight)) dx = Mathf.Clamp(rt.rect.xMax - inkRight, -80f, 80f);
                }
                rt.anchoredPosition = new Vector2(dx, up ? y : -y);
                y += h + spacing;
            }
        }
    }

    // ---------- the minimap at the bottom right ----------
    // With a HUD layout that puts it there, the small minimap moves down to the bottom right corner, as far from the
    // bottom edge as it normally is from the top. It is moved as one piece (its frame, the map and the biome name) by an
    // offset on top of wherever the game puts it, and goes back the moment the layout changes or keyboard or mouse
    // takes over. The gameplay hints are measured from the map, so they follow it.
    static class MapPlace
    {
        // The minimap's own place is noted the moment it is first moved, and it is put back exactly there when the
        // preset no longer wants it at the bottom (an earlier version worked out the way back from its own offset, and
        // left the map at the bottom when switching from Bottom HUD to another preset). The move itself is made just
        // before the screen is drawn.
        public static bool Moved;
        // The minimap picture's distance from the right edge and from the top edge of the screen (where the game has it, even
        // while it is moved down: moved, its bottom keeps that same distance), in screen pixels. Every corner piece of the
        // HUD (resource bars, hotbar) keeps these, so their outer edges line up with the map's (0.73.0; before, some measured
        // the map's frame, which sits a little further out than the picture).
        static readonly Vector3[] gc = new Vector3[4];
        public static bool PicGaps(out float right, out float top)
        {
            right = top = 0f;
            Minimap mm = Minimap.instance;
            RectTransform pic = mm != null && mm.m_mapImageSmall != null ? mm.m_mapImageSmall.rectTransform : null;
            if (pic == null || !pic.gameObject.activeInHierarchy) return false;
            pic.GetWorldCorners(gc);
            right = Screen.width - gc[2].x; top = Screen.height - (gc[2].y - ShiftY);
            return right >= 0f && right < Screen.width * 0.25f && top >= 0f && top < Screen.height * 0.25f;
        }
        public static float ShiftY;   // how far it is moved, in screen pixels (negative is down)
        static readonly Vector3[] c = new Vector3[4];
        static bool hooked, want, logged;
        static Vector2 home, effectsHome;
        static RectTransform movedRoot, movedEffects;

        // The status effects sit to the left of the minimap in the top right corner. With the map at the bottom they
        // move right into the corner, ending as far from the right edge as the map did, and go back with it.
        static void PlaceEffects(RectTransform map, bool on)
        {
            Hud h = Hud.instance;
            RectTransform fx = h != null ? h.m_statusEffectListRoot : null;
            if (fx != movedEffects && movedEffects != null) movedEffects = null;
            if (fx == null) return;
            if (!on) { if (movedEffects != null) { fx.anchoredPosition = effectsHome; movedEffects = null; } return; }
            if (movedEffects == null) { effectsHome = fx.anchoredPosition; movedEffects = fx; }
            fx.anchoredPosition = effectsHome;
            // the right end of what the effects draw, against the right end of the map at its own place
            float right = float.MinValue;
            foreach (RectTransform ch in fx) { if (!ch.gameObject.activeInHierarchy) continue; ch.GetWorldCorners(c); right = Mathf.Max(right, c[2].x); }
            if (right == float.MinValue) { fx.GetWorldCorners(c); right = c[2].x; }
            map.GetWorldCorners(c);
            float shift = c[2].x - right;
            float scale = fx.parent != null ? Mathf.Max(0.0001f, fx.parent.lossyScale.x) : 1f;
            if (shift > 0f) fx.anchoredPosition = effectsHome + new Vector2(shift / scale, 0f);
        }

        public static void Tick()
        {
            if (!hooked) { hooked = true; Canvas.willRenderCanvases += Apply; }
            bool pad = Plugin.LayoutOn;   // the controller, or keyboard and mouse with the preset applied to them
            want = pad && Plugin.MinimapBottom;
            hide = pad && !Plugin.ShowMinimap.Value;
        }

        // Switched off in the settings: the map is made invisible but keeps its place, so everything measured from it
        // (its gameplay hints, the resource bars' edge distances) stays put; the hints move up into its place.
        public static bool Hidden;
        static bool hide;
        static CanvasGroup fade;
        static void Hide(RectTransform root, bool on)
        {
            if (!on)
            {
                // shown again: brought back once, then left alone, so the fade behind the inventory (which uses the same
                // see-through value) works again. Setting it every frame had stopped that fade after the map was switched
                // off and on.
                if (Hidden && fade != null && fade.transform == root) { fade.alpha = 1f; fade.blocksRaycasts = true; }
                Hidden = false;
                return;
            }
            if (fade == null || fade.transform != root)
            {
                fade = root.GetComponent<CanvasGroup>();
                if (fade == null) fade = root.gameObject.AddComponent<CanvasGroup>();
            }
            if (fade.alpha != 0f) fade.alpha = 0f;
            fade.blocksRaycasts = false;
            Hidden = true;
        }

        static void Apply()
        {
            long pt = Perf.Now();
            try
            {
                Minimap mm = Minimap.instance;
                RectTransform root = mm != null && mm.m_smallRoot != null ? mm.m_smallRoot.transform as RectTransform : null;
                if (root != movedRoot) { Moved = false; ShiftY = 0f; movedRoot = null; }   // a new map (a new world): nothing of ours on it
                if (root == null) return;
                if (!logged)
                {
                    logged = true;
                    Transform par = root.parent;
                    string kids = ""; foreach (Transform k in root) kids += " [" + k.name + "]";
                    Plugin.Log.LogInfo("Minimap place: small map '" + root.name + "' under '" + (par != null ? par.name : "-") + "'"
                        + (par != null && par.GetComponent<LayoutGroup>() != null ? " (a layout group places it)" : "")
                        + ", picture inside it: " + (mm.m_mapImageSmall != null && mm.m_mapImageSmall.transform.IsChildOf(root)) + ", parts:" + kids);
                }
                Hide(root, hide);
                RectTransform pic = mm.m_mapImageSmall != null && mm.m_mapImageSmall.transform.IsChildOf(root) ? mm.m_mapImageSmall.rectTransform : root;
                if (!want)
                {
                    if (Moved) { root.anchoredPosition = home; Moved = false; ShiftY = 0f; movedRoot = null; Plugin.Log.LogInfo("Minimap place: back to its own place"); }
                    // with the map hidden the status effects take the corner it leaves, as when it moves down
                    PlaceEffects(pic, hide && root.gameObject.activeInHierarchy);
                    return;
                }
                if (!root.gameObject.activeInHierarchy) return;   // the large map is open: leave it be
                if (!Moved) { home = root.anchoredPosition; movedRoot = root; Plugin.Log.LogInfo("Minimap place: moving it to the bottom right (its own place " + home + ")"); }
                // measured at its own place each time, then moved down to the same distance from the bottom edge
                root.anchoredPosition = home;
                float scale = root.parent != null ? Mathf.Max(0.0001f, root.parent.lossyScale.y) : 1f;
                PlaceEffects(pic, true);   // measured against the map at its own place, before it moves down
                pic.GetWorldCorners(c);
                float gap = Screen.height - c[2].y;
                float down = Mathf.Clamp(c[0].y - gap, 0f, Screen.height);
                root.anchoredPosition = home - new Vector2(0f, down / scale);
                Moved = true; ShiftY = -down;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Minimap place: " + e.Message); }
            finally { Perf.Lap("minimap move", pt); }
        }
    }

    // ---------- button remaps in the world ----------
    // Three changes to how buttons reach actions the game already has. Nothing new can be done, only reached sooner.
    //   B tap    dodge, on its own. Vanilla only dodges on B while block is held (or while sneaking).
    //   B hold   toggle sneak. Vanilla has it on a stick click.
    //   LB + RB  pressed together: the Forsaken power.
    // B has both a tap and a hold, so the dodge fires when B is let go, not when it goes down.
    // The buttons are read here every frame; the results are handed to the game where it takes the player's controls.
    static class Remap
    {
        public static bool Active;   // B belongs to the mod right now
        public static bool Chord;    // LB and RB went down together: neither does its own job until both are let go
        public static bool Grace;    // RB has just gone down and is waiting a moment to see whether LB joins it

        static bool pendingDodge, pendingCrouch, pendingRb, doDodge;
        static bool bWas, bTracking, bFired, lbWas, rbWas, powerDone;
        static float bStart, lbAt = -10f, rbAt = -10f, chordAt, graceAt;
        static readonly MethodInfo M_Dodge = AccessTools.Method(typeof(Player), "Dodge");
        static readonly MethodInfo M_TakeInput = AccessTools.Method(typeof(Player), "TakeInput");
        static readonly FieldInfo F_moveDir = AccessTools.Field(typeof(Character), "m_moveDir");
        static readonly FieldInfo F_lookDir = AccessTools.Field(typeof(Character), "m_lookDir");

        public static void Reset() { Active = Chord = Grace = pendingDodge = pendingCrouch = pendingRb = doDodge = bTracking = NoCrouch = false; }

        // The remaps belong to the PRO layout. In the game's own three layouts every button keeps its vanilla job.
        public static bool DodgeLayout() { return Plugin.RemapDodge.Value && Pro.On; }
        // The right stick click sheathes instead of sneaking: PRO only, and only while sneak is on holding B
        public static bool SheatheLayout() { return Plugin.SheatheOnRs.Value && DodgeLayout(); }

        // NoCrouch: the game's crouch button is the mod's right now (it sheathes), so the game does not see it
        public static bool NoCrouch;
        static bool rsWas;

        // The game's own sheathe, as its hide button does it: whatever is in hand is put away (not mid-swing or mid-dodge);
        // with empty hands, what was put away comes back out (not mid-dodge, and only on the ground when swimming).
        static readonly MethodInfo M_RightItem = AccessTools.Method(typeof(Humanoid), "GetRightItem");
        static readonly MethodInfo M_LeftItem = AccessTools.Method(typeof(Humanoid), "GetLeftItem");
        static readonly MethodInfo M_ShowHands = AccessTools.Method(typeof(Humanoid), "ShowHandItems");
        static void Sheathe(Player p)
        {
            if (M_RightItem == null || M_LeftItem == null || M_ShowHands == null) return;
            if (M_RightItem.Invoke(p, null) != null || M_LeftItem.Invoke(p, null) != null)
            {
                if (!p.InAttack() && !p.InDodge()) p.HideHandItems(false, true);
            }
            else if ((!p.IsSwimming() || p.IsOnGround()) && !p.InDodge()) M_ShowHands.Invoke(p, new object[] { false, true });
        }

        static bool PowerReady(Player p)
        {
            StatusEffect se; float cooldown;
            p.GetGuardianPowerHUD(out se, out cooldown);
            return se != null && cooldown <= 0f;
        }

        public static void Update()
        {
            Player p = Player.m_localPlayer;
            bool input = Plugin.Enabled.Value && p != null && ZInput.IsGamepadActive() && !ModSettings.Blocking
                      && !InventoryGui.IsVisible() && !Hud.InRadial() && !Hud.IsPieceSelectionVisible()
                      && M_TakeInput != null && (bool)M_TakeInput.Invoke(p, null);
            float now = Time.unscaledTime;

            // ----- B: tap dodges, hold toggles sneak -----
            // Not with the hammer out (B opens the build menu there) and not while LT is held (B is a favorite then).
            Active = input && DodgeLayout() && M_Dodge != null && !p.InPlaceMode() && !Core.FavFace;
            bool b = Core.RawHeld("JoyDodge");
            bool bDown = b && !bWas;
            bWas = b;
            if (!Active) bTracking = false;
            else
            {
                if (bDown) { bTracking = true; bFired = false; bStart = now; }
                if (bTracking)
                {
                    if (b && !bFired && now - bStart >= Mathf.Max(0.1f, Plugin.SneakHold.Value)) { bFired = true; pendingCrouch = true; }
                    if (!b) { if (!bFired) pendingDodge = true; bTracking = false; }
                }
            }

            // ----- right stick click: sheathe -----
            // With the hammer out the click stays the game's own (B opens the build menu there, so it could not sneak).
            bool sheathe = p != null && Plugin.Enabled.Value && ZInput.IsGamepadActive() && SheatheLayout() && !p.InPlaceMode();
            NoCrouch = sheathe;
            bool rs = Core.RawHeld("JoyCrouch");
            bool rsDown = rs && !rsWas;
            rsWas = rs;
            if (sheathe && input && rsDown && !ZInput.GetButton("JoyAltKeys")) Sheathe(p);

            // ----- LB + RB together: Forsaken power -----
            bool lb = Core.RawHeld("JoyLBumper"), rb = Core.RawHeld("JoyRBumper");
            bool rbDown = rb && !rbWas;
            if (lb && !lbWas) lbAt = now;
            if (rbDown) rbAt = now;
            lbWas = lb; rbWas = rb;
            // With the hammer out, LB + RB is the game's own "copy this piece". That comes first: the Forsaken power
            // cannot be used from the bumpers while building, and neither bumper is held back or delayed.
            if (!input || !Plugin.PowerChord.Value || !Pro.On || p.InPlaceMode()) { Chord = Grace = false; return; }

            float window = Mathf.Max(0.02f, Plugin.ChordWindow.Value), grace = Mathf.Min(Plugin.ChordGrace.Value, window);
            if (!Chord && lb && rb && Mathf.Abs(lbAt - rbAt) <= window) { Chord = true; Grace = false; powerDone = false; chordAt = now; }
            if (Chord)
            {
                if (!lb && !rb) Chord = false;
                else if (!powerDone && lb && rb)
                {
                    // Not ready: one call, so the game says so itself. Ready but the character is busy (mid-swing, say):
                    // keep asking for a moment, so the power still comes out as soon as it can.
                    if (!PowerReady(p)) { p.StartGuardianPower(); powerDone = true; }
                    else if (p.StartGuardianPower() || now - chordAt > 1.5f) powerDone = true;
                }
                return;
            }
            // RB on its own: while a power is ready, hold its job back for a moment in case LB is on its way down too.
            // If LB has already been held for a while this cannot become the combination, so nothing waits.
            if (rbDown && grace > 0f && !(lb && now - lbAt > window) && PowerReady(p)) { Grace = true; graceAt = now; }
            if (Grace && now - graceAt >= grace) { Grace = false; pendingRb = true; }   // LB never came: RB does its job after all
        }

        // Called as the game takes the player's controls for this step.
        public static void Controls(ref bool attack, ref bool attackHold, ref bool secondaryAttack, ref bool secondaryAttackHold,
                                    ref bool block, ref bool blockHold, ref bool crouch, ref bool dodge)
        {
            bool rbIsAttack = (int)ZInput.InputLayout == 1;   // Alternative 1: RB attacks and LB blocks. Otherwise RB is the secondary attack.
            if (Chord)
            {
                if (rbIsAttack) attack = attackHold = block = blockHold = false;
                else secondaryAttack = secondaryAttackHold = false;
            }
            else if (Grace)
            {
                if (rbIsAttack) attack = attackHold = false;
                else secondaryAttack = secondaryAttackHold = false;
            }
            else if (pendingRb)
            {
                pendingRb = false;
                if (rbIsAttack) attack = attackHold = true;
                else secondaryAttack = secondaryAttackHold = true;
            }
            if (Active || pendingDodge) dodge = false;   // B is the mod's: the game's own "B while blocking" dodge would fire on the press and double up
            if (pendingCrouch) { pendingCrouch = false; crouch = true; }
            doDodge = pendingDodge;
            pendingDodge = false;
        }

        // After the game has worked out which way the character is moving: dodge that way, or step back if standing still.
        public static void AfterControls(Player p)
        {
            if (!doDodge) return;
            doDodge = false;
            if (M_Dodge == null || F_moveDir == null || F_lookDir == null) return;
            Vector3 dir = (Vector3)F_moveDir.GetValue(p);
            if (dir.magnitude < 0.1f)
            {
                dir = -(Vector3)F_lookDir.GetValue(p);
                dir.y = 0f;
                dir.Normalize();
            }
            M_Dodge.Invoke(p, new object[] { dir });   // the game's own dodge: stamina, timing and rules are untouched
        }
    }

    // ---------- the PRO controller layout ----------
    // A fourth choice in the game's own controller layout setting, next to Classic and the two Alternatives, and the
    // one the mod starts on. Underneath it is the game's Alternative 1 layout with the mod's remaps on top (B dodges,
    // hold B sneaks, LB + RB uses the Forsaken power). Picking any of the game's three layouts turns the remaps off
    // and gives back vanilla buttons; the rest of the mod (inventory, favorites, hints) works in every layout.
    static class Pro
    {
        public const string Name = "Controller Support Plus";   /* 1.0.1 (Rob): "PRO controller" before; the saved choice (UseProLayout) keeps its name, so players on it stay on it */
        const int Alt1 = 1;
        public static bool On { get { return Plugin.Enabled.Value && Plugin.ProLayout.Value && (int)ZInput.InputLayout == Alt1; } }

        static bool checkedAtStart;
        public static bool WasOnAtOpen;   // what to go back to if the settings are closed with Back

        // PRO needs Alternative 1 underneath. Once per session, if PRO is the chosen layout, make sure that is so.
        public static void Update()
        {
            if (checkedAtStart || ZInput.instance == null) return;
            checkedAtStart = true;
            if (Plugin.Enabled.Value && Plugin.ProLayout.Value && (int)ZInput.InputLayout != Alt1)
            {
                ZInput.instance.ChangeInputLayout((InputLayout)Alt1);
                Plugin.Log.LogInfo("PRO controller layout: switched the game's layout to Alternative 1, which it is built on");
            }
        }

        public static Type SettingsType() { return AccessTools.TypeByName("Valheim.SettingsGui.GamepadSettings"); }
        // One step of the layout setting. The order is PRO, Classic, Alternative 1, Alternative 2, and round again.
        // Returns false when it could not be done, so the game's own stepping runs instead.
        public static bool Step(object settings, int dir)
        {
            if (!Plugin.Enabled.Value || settings == null) return false;
            FieldInfo current = AccessTools.Field(settings.GetType(), "m_currentControllerLayout");
            MethodInfo changed = AccessTools.Method(settings.GetType(), "OnLayoutChanged");
            if (current == null || changed == null) return false;
            int place = Plugin.ProLayout.Value ? 0 : (int)(InputLayout)current.GetValue(settings) + 1;
            place = (place + dir + 4) % 4;
            Plugin.ProLayout.Value = place == 0;
            current.SetValue(settings, (InputLayout)(place == 0 ? Alt1 : place - 1));
            changed.Invoke(settings, null);
            return true;
        }
    }

    [HarmonyPatch]
    static class Patch_SettingsLayoutRight
    {
        static MethodBase TargetMethod() { Type t = Pro.SettingsType(); return t != null ? AccessTools.Method(t, "OnControllerLayoutRight") : null; }
        static bool Prepare() { return TargetMethod() != null; }
        static bool Prefix(object __instance)
        {
            try { return !Pro.Step(__instance, 1); } catch (Exception e) { Plugin.Log.LogWarning("PRO layout step: " + e.Message); return true; }
        }
    }

    [HarmonyPatch]
    static class Patch_SettingsLayoutLeft
    {
        static MethodBase TargetMethod() { Type t = Pro.SettingsType(); return t != null ? AccessTools.Method(t, "OnControllerLayoutLeft") : null; }
        static bool Prepare() { return TargetMethod() != null; }
        static bool Prefix(object __instance)
        {
            try { return !Pro.Step(__instance, -1); } catch (Exception e) { Plugin.Log.LogWarning("PRO layout step: " + e.Message); return true; }
        }
    }
    [HarmonyPatch]
    static class Patch_SettingsInitialize
    {
        static MethodBase TargetMethod() { Type t = Pro.SettingsType(); return t != null ? AccessTools.Method(t, "Initialize") : null; }
        static bool Prepare() { return TargetMethod() != null; }
        static void Prefix() { Pro.WasOnAtOpen = Plugin.ProLayout.Value; }
    }

    [HarmonyPatch]
    static class Patch_SettingsBack
    {
        static MethodBase TargetMethod() { Type t = Pro.SettingsType(); return t != null ? AccessTools.Method(t, "OnBack") : null; }
        static bool Prepare() { return TargetMethod() != null; }
        static void Prefix() { if (Plugin.ProLayout.Value != Pro.WasOnAtOpen) Plugin.ProLayout.Value = Pro.WasOnAtOpen; }   // Back undoes the layout choice, PRO included
    }

    // the name shown in the layout setting
    [HarmonyPatch]
    static class Patch_SettingsLayoutName
    {
        static MethodBase TargetMethod() { Type t = Pro.SettingsType(); return t != null ? AccessTools.Method(t, "OnZInputLayoutChanged") : null; }
        static bool Prepare() { return TargetMethod() != null; }
        static void Postfix(object __instance)
        {
            try
            {
                if (!Pro.On) return;
                FieldInfo f = AccessTools.Field(__instance.GetType(), "m_controllerLayoutText");
                TMP_Text t = f != null ? f.GetValue(__instance) as TMP_Text : null;
                if (t != null) t.text = Pro.Name;
            }
            catch (Exception e) { Plugin.Log.LogWarning("PRO layout name: " + e.Message); }
        }
    }

    // the picture of the controller in the settings: B carries its two PRO jobs
    [HarmonyPatch]
    static class Patch_GamepadMap
    {
        static Type MapType() { return AccessTools.TypeByName("Valheim.SettingsGui.GamepadMap") ?? AccessTools.TypeByName("GamepadMap"); }
        static bool loggedRs;
        static TMP_Text MapLabel(object map, string field)
        {
            FieldInfo f = AccessTools.Field(map.GetType(), field);
            object label = f != null ? f.GetValue(map) : null;
            PropertyInfo p = label != null ? AccessTools.Property(label.GetType(), "Label") : null;
            return p != null ? p.GetValue(label, null) as TMP_Text : null;
        }
        /* the button glyph beside a label (its Button text) */
        static TMP_Text MapButton(object map, string field)
        {
            FieldInfo f = AccessTools.Field(map.GetType(), field);
            object label = f != null ? f.GetValue(map) : null;
            PropertyInfo p = label != null ? AccessTools.Property(label.GetType(), "Button") : null;
            return p != null ? p.GetValue(label, null) as TMP_Text : null;
        }
        static bool loggedGp;
        static MethodBase TargetMethod() { Type t = MapType(); return t != null ? AccessTools.Method(t, "UpdateMap") : null; }
        static bool Prepare() { return TargetMethod() != null; }
        // The game translates every label on the picture again when it is shown, which put its own words back over
        // these. So each PRO label holds its own text, every frame it is on screen (ProLabel below).
        static void Postfix(object __instance)
        {
            try
            {
                bool pro = Pro.On && Plugin.RemapDodge.Value;
                TMP_Text b = MapLabel(__instance, "joyButton1");    // B
                TMP_Text rs = MapLabel(__instance, "joyButton9");   // the right stick click
                if (!loggedRs) { loggedRs = true; Plugin.Log.LogInfo("PRO layout map: shown (PRO " + (pro ? "on" : "off") + "), B read '" + (b != null ? b.text : "-") + "', right stick click read '" + (rs != null ? rs.text : "-") + "'"); }
                ProLabel.Hold(b, pro ? "Dodge / Hold: Sneak" : null);
                ProLabel.Hold(rs, pro && Remap.SheatheLayout() && rs != null && !string.IsNullOrEmpty(rs.text) ? "Sheathe" : null);
                Radial(__instance, Pro.On && Plugin.FavEnabled.Value && Favorites.SecondaryAvailable());
                /* the Forsaken power is both bumpers in PRO: the picture drew the two sticks beside it */
                TMP_Text gp = MapButton(__instance, "joyAxis9And10");
                if (!loggedGp && gp != null) { loggedGp = true; Plugin.Log.LogInfo("PRO layout map: Forsaken power button read '" + gp.text + "'"); }
                ProLabel.Hold(gp, Pro.On ? Hints.Glyph("JoyLBumper") + " + " + Hints.Glyph("JoyRBumper") : null);
            }
            catch (Exception e) { Plugin.Log.LogWarning("PRO layout map: " + e.Message); }
        }

        // With the Controller Hotbar's second set, LT is no longer the radial wheel: held, it turns the face buttons into
        // that set. LT says so; any other label that names the radial wheel loses that part.
        static readonly string[] Fields = { "joyButton0", "joyButton1", "joyButton2", "joyButton3", "joyButton4", "joyButton5", "joyButton6", "joyButton7",
            "joyAxis9", "joyAxis10", "joyAxis9And10", "joyButton8", "joyButton9", "joyAxis1And2", "joyAxis4And5", "joyAxis6And7",
            "joyAxis6Left", "joyAxis6Right", "joyAxis6LeftRight", "joyAxis7Up", "joyAxis7Down" };
        static bool loggedAll;
        static void Radial(object map, bool on)
        {
            System.Text.StringBuilder log = loggedAll ? null : new System.Text.StringBuilder();
            foreach (string f in Fields)
            {
                TMP_Text t = MapLabel(map, f);
                if (t == null) continue;
                ProLabel held = t.GetComponent<ProLabel>();
                if (log != null) log.Append(" | " + f + ": " + t.text);
                if (f == "joyButton1" || f == "joyButton9") continue;   // B and the right stick click have their own PRO labels
                string want = null;
                /* 1.0.1 (Rob): on the Controller Support Plus layout the left stick click's greyed second job (auto-pickup, with
                   the alternate key held) is off, so its legend goes; the greyed parts are the alternate key's jobs */
                bool noPickup = Pro.On && f == "joyButton8" && t.text.IndexOf("<color=", StringComparison.OrdinalIgnoreCase) >= 0;
                if (on && f == "joyAxis9") want = "Hold: Quick slots";   // LT (Rob's name for the second set, 2026-10-31)
                else if ((on && t.text.IndexOf("adial", StringComparison.OrdinalIgnoreCase) >= 0) || noPickup)
                {
                    // the label without its radial part ("Radial / Something" keeps "Something"; radial alone goes blank)
                    List<string> keep = new List<string>();
                    foreach (string part in t.text.Split(new[] { " / " }, StringSplitOptions.None))   /* split on " / " only (1.0.1): a "</color>" holds a slash too, and a lone "/" cut it, leaving "color>" on the left stick */
                    {
                        if (part.Trim().Length == 0) continue;
                        if (on && part.IndexOf("adial", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (noPickup && part.IndexOf("<color=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        keep.Add(part.Trim());
                    }
                    want = keep.Count > 0 ? string.Join(" / ", keep.ToArray()) : " ";
                }
                if (want != null || held != null) ProLabel.Hold(t, want);
            }
            if (log != null) { loggedAll = true; Plugin.Log.LogInfo("Controller picture labels:" + log); }
        }
    }

    // Keeps one label of the controller picture on the PRO text while it is on screen. With no text to hold it does
    // nothing, and the game's own text comes back the next time the picture is filled.
    class ProLabel : MonoBehaviour
    {
        TMP_Text label;
        string want;
        public static void Hold(TMP_Text t, string text)
        {
            if (t == null) return;
            ProLabel p = t.GetComponent<ProLabel>();
            if (p == null) { if (text == null) return; p = t.gameObject.AddComponent<ProLabel>(); p.label = t; }
            p.want = text;
            if (text != null && t.text != text) t.text = text;
        }
        void LateUpdate()
        {
            if (want != null && label != null && label.text != want) label.text = want;
        }
    }

    [HarmonyPatch(typeof(Player), "SetControls")]
    static class Patch_SetControls
    {
        static void Prefix(Player __instance, ref bool attack, ref bool attackHold, ref bool secondaryAttack, ref bool secondaryAttackHold,
                           ref bool block, ref bool blockHold, ref bool crouch, ref bool dodge)
        {
            if (__instance != Player.m_localPlayer) return;
            try { Remap.Controls(ref attack, ref attackHold, ref secondaryAttack, ref secondaryAttackHold, ref block, ref blockHold, ref crouch, ref dodge); }
            catch (Exception e) { Remap.Reset(); Core.Fail(e); }
        }
        static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            try { Remap.AfterControls(__instance); } catch (Exception e) { Remap.Reset(); Core.Fail(e); }
        }
    }

    // ---------- controller cursor in the build menu ----------
    // The same idea as in the inventory: using the left stick inside the build menu brings up a cursor, and using the
    // D-pad puts it away again. Whatever button the cursor is over (a piece, a tag, a tab) becomes the build menu's own
    // selection, so the piece details and the menu's other buttons (favorite, close) work on it unchanged. A presses it.
    // The right stick scrolls the list the cursor is over.
    // The build menu moves its selection through Unity's own menu navigation, not through the game's button reads, so
    // while the cursor is up that navigation is switched off, and switched back on the moment the cursor goes away.
    static class BuildCursor
    {
        public static bool On;
        static Vector2 pos;
        static GameObject root;
        static Selectable[] targets;
        static ScrollRect[] lists;
        static float lookAt;
        static Selectable hover;
        static bool overTarget, tookNav;
        static BuildUi builtFor;
        static readonly Vector3[] corners = new Vector3[4];

        public static void Stop()
        {
            if (tookNav && EventSystem.current != null) EventSystem.current.sendNavigationEvents = true;
            tookNav = false;
            On = false; hover = null; overTarget = false;
            if (root != null && root.activeSelf) root.SetActive(false);
        }

        static bool Over(RectTransform rt) { return rt != null && rt.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rt, pos, null); }

        public static void Update()
        {
            Hud hud = Hud.instance;
            BuildUi ui = hud != null ? hud.m_buildUi : null;
            bool open = Plugin.Enabled.Value && Plugin.CursorEnabled.Value && ui != null && ui.gameObject.activeInHierarchy
                     && ZInput.IsGamepadActive() && !ui.SearchFieldFocused && EventSystem.current != null;
            if (!open) { if (On || tookNav) Stop(); return; }
            EventSystem es = EventSystem.current;

            Vector2 stick = ZInput.GetJoyLeftStick();
            float mag = stick.magnitude;
            if (!On)
            {
                if (mag < 0.35f) return;
                On = true;
                // start from whatever the build menu has selected
                GameObject sel = es.currentSelectedGameObject;
                RectTransform from = sel != null && sel.transform.IsChildOf(ui.transform) ? sel.transform as RectTransform : null;
                if (from != null) { from.GetWorldCorners(corners); pos = (corners[0] + corners[2]) * 0.5f; }
                else pos = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                es.sendNavigationEvents = false; tookNav = true;
            }
            else if (Core.RawHeld("JoyDPadLeft") || Core.RawHeld("JoyDPadRight") || Core.RawHeld("JoyDPadUp") || Core.RawHeld("JoyDPadDown"))
            {
                Stop();   // the D-pad takes over again
                return;
            }
            if (es.sendNavigationEvents) es.sendNavigationEvents = false;

            // slower over something that can be pressed, for precision; faster across the rest of the menu
            float dt = Time.unscaledDeltaTime;
            if (mag > 0.15f)
            {
                float speed = (overTarget ? Plugin.CursorSlotSpeed.Value : Plugin.CursorPanelSpeed.Value) * (Screen.height / 1080f) * Mathf.Pow(Mathf.Min(1f, mag), 1.6f);
                pos += stick / mag * speed * dt;
                pos.x = Mathf.Clamp(pos.x, 4f, Screen.width - 4f); pos.y = Mathf.Clamp(pos.y, 4f, Screen.height - 4f);
            }

            if (builtFor != ui || targets == null || Time.unscaledTime > lookAt)
            {
                builtFor = ui;
                targets = ui.GetComponentsInChildren<Selectable>(false);
                lists = ui.GetComponentsInChildren<ScrollRect>(false);
                lookAt = Time.unscaledTime + 0.4f;   // the menu rebuilds its buttons as tags and searches change
            }
            Selectable best = null; float bestArea = float.MaxValue;
            foreach (Selectable s in targets)
            {
                if (s == null || !s.isActiveAndEnabled || !s.interactable) continue;
                if (s is TMP_InputField || s is InputField || s is Scrollbar || s is Slider) continue;   // not things to point and press at
                RectTransform rt = s.transform as RectTransform;
                if (!Over(rt)) continue;
                float area = Mathf.Abs(rt.rect.width * rt.rect.height);
                if (area < bestArea) { best = s; bestArea = area; }   // the smallest one under the cursor is the one meant
            }
            if (best != null && best != hover) best.Select();   // the build menu's own selection follows the cursor
            hover = best; overTarget = best != null;
            if (hover != null && Core.RawDown("JoyButtonA"))
                ExecuteEvents.Execute(hover.gameObject, new BaseEventData(es), ExecuteEvents.submitHandler);   // the same as pressing it from the D-pad

            // Lists stay where they are while the cursor is up: selecting what the cursor passes over must not scroll
            // them. Only the right stick moves the list the cursor is over.
            Scroll.Pin(lists);
            float ry = ZInput.GetJoyRightStick().y;
            if (Mathf.Abs(ry) >= 0.2f && lists != null && Plugin.ScrollSpeed.Value > 0f)
            {
                ScrollRect list = null; float listArea = float.MaxValue;
                foreach (ScrollRect r in lists)
                {
                    if (r == null || !r.isActiveAndEnabled || r.content == null || !r.vertical) continue;
                    RectTransform view = r.viewport != null ? r.viewport : r.transform as RectTransform;
                    if (r.content.rect.height - view.rect.height <= 1f || !Over(view)) continue;
                    float area = Mathf.Abs(view.rect.width * view.rect.height);
                    if (area < listArea) { list = r; listArea = area; }
                }
                if (list != null) Scroll.Move(list, ry * Plugin.ScrollSpeed.Value * dt);
            }
            Scroll.Hold();
            // the cursor itself: the same ring and dot as in the inventory
            if (root == null)
            {
                Vector2 mid = new Vector2(0.5f, 0.5f);
                root = Hints.New("ControllerPro_BuildCursor", ui.transform);
                Hints.Place(root, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
                Image shade = Hints.Img(Hints.New("shade", root.transform), Hints.Ring(), new Color(0f, 0f, 0f, 0.7f));
                Hints.Place(shade.gameObject, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
                Image ring = Hints.Img(Hints.New("ring", root.transform), Hints.Ring(), Color.white);
                Hints.Place(ring.gameObject, mid, mid, mid, Vector2.zero, new Vector2(26f, 26f));
                Image dot = Hints.Img(Hints.New("dot", root.transform), Hints.Disc(), Color.white);
                Hints.Place(dot.gameObject, mid, mid, mid, Vector2.zero, new Vector2(5f, 5f));
            }
            root.SetActive(true);
            root.transform.SetAsLastSibling();
            root.transform.position = new Vector3(pos.x, pos.y, root.transform.position.z);
            CursorLook.Hover(root, ref hoverK, overTarget);
        }
        static float hoverK;
    }

    // ---------- scrolling lists with the right stick ----------
    // The right stick scrolls the list the cursor is over. With the cursor away, or over something that does not
    // scroll, it scrolls the list of the panel in use (the recipe list in crafting, a large chest's slots). The game's
    // own dialogs (skills, texts) already scroll on the right stick, so this fills the gap in the inventory itself.
    //
    // While a cursor is up (here or in the build menu), lists only move when the right stick moves them. The game
    // scrolls a list by itself to bring whatever is selected into view. That is right for the D-pad, but with a
    // cursor the selection is whatever the cursor is passing over, so lists ran away under it: move to the lower half
    // and the list scrolled to the bottom. So every list's place is held while a cursor is up, and let go when it goes.
    static class Scroll
    {
        static ScrollRect[] lists;
        static float listsAt;
        static readonly System.Collections.Generic.Dictionary<ScrollRect, float> held = new System.Collections.Generic.Dictionary<ScrollRect, float>();
        static readonly System.Collections.Generic.List<ScrollRect> gone = new System.Collections.Generic.List<ScrollRect>();

        // start holding any list not held yet, at wherever it is now
        public static void Pin(ScrollRect[] all)
        {
            if (all == null) return;
            foreach (ScrollRect r in all)
                if (r != null && r.vertical && !held.ContainsKey(r)) held[r] = r.verticalNormalizedPosition;
        }

        // the right stick's own move: by so many of the list's units
        public static void Move(ScrollRect r, float by)
        {
            RectTransform view = r.viewport != null ? r.viewport : r.transform as RectTransform;
            float span = r.content.rect.height - view.rect.height;
            if (span <= 1f) return;
            float at;
            if (!held.TryGetValue(r, out at)) at = r.verticalNormalizedPosition;
            at = Mathf.Clamp01(at + by / span);
            if (held.ContainsKey(r)) held[r] = at;
            r.verticalNormalizedPosition = at;
        }

        // put every held list back where it is meant to be (runs after the game has had its say each frame)
        public static void Hold()
        {
            if (held.Count == 0) return;
            foreach (System.Collections.Generic.KeyValuePair<ScrollRect, float> kv in held)
            {
                ScrollRect r = kv.Key;
                if (r == null) { gone.Add(r); continue; }
                if (!r.isActiveAndEnabled) continue;
                if (Mathf.Abs(r.verticalNormalizedPosition - kv.Value) > 0.0005f) r.verticalNormalizedPosition = kv.Value;
                r.velocity = Vector2.zero;
            }
            if (gone.Count > 0) { foreach (ScrollRect r in gone) held.Remove(r); gone.Clear(); }
        }

        public static void Release() { if (held.Count > 0) held.Clear(); }

        public static void Tick(InventoryGui gui)
        {
            if (lists == null || Time.unscaledTime > listsAt)
            {
                lists = gui.GetComponentsInChildren<ScrollRect>(false);
                listsAt = Time.unscaledTime + 0.5f;
            }
            if (Cursor.On) Pin(lists);
            float y = ZInput.GetJoyRightStick().y;
            if (Mathf.Abs(y) >= 0.2f && Plugin.ScrollSpeed.Value > 0f)
            {
                ScrollRect under = null, inPanel = null;
                float underArea = float.MaxValue;
                RectTransform panel = Dim.CurrentPanel;
                foreach (ScrollRect r in lists)
                {
                    if (r == null || !r.isActiveAndEnabled || r.content == null || !r.vertical) continue;
                    RectTransform view = r.viewport != null ? r.viewport : r.transform as RectTransform;
                    if (r.content.rect.height - view.rect.height <= 1f) continue;   // everything already fits: nothing to scroll
                    if (Cursor.On && Cursor.Over(view))
                    {
                        float area = Mathf.Abs(view.rect.width * view.rect.height);
                        if (area < underArea) { under = r; underArea = area; }
                    }
                    if (inPanel == null && panel != null && r.transform.IsChildOf(panel)) inPanel = r;
                }
                ScrollRect list = under != null ? under : inPanel;
                // pushing the stick up brings the top of the list back; the speed is in the list's own units per second
                if (list != null) Move(list, y * Plugin.ScrollSpeed.Value * Time.unscaledDeltaTime);
            }
            if (Cursor.On) Hold();
        }
    }
    // ---------- transfer all but one ----------
    // LT + RT with a chest open: the focused stack goes across to the other container, one left behind. It fills the other
    // side's stacks of the same item first, then empty slots in the order the game's own transfer uses, through the game's
    // own move (Inventory.MoveItemToThis), so nothing is made or lost and the container saves as usual.
    static class AllButOne
    {
        static readonly MethodInfo M_Empty = AccessTools.Method(typeof(Inventory), "FindEmptySlot");
        static readonly MethodInfo M_TopFirst = AccessTools.Method(typeof(Inventory), "TopFirst");
        public static int Move(Inventory from, Inventory to, ItemDrop.ItemData item, int want)
        {
            if (from == null || to == null || item == null || want <= 0) return 0;
            int max = Mathf.Max(1, item.m_shared.m_maxStackSize), moved = 0;
            foreach (ItemDrop.ItemData t in new List<ItemDrop.ItemData>(to.GetAllItems()))
            {
                if (want <= 0) break;
                if (t == item || t.m_shared.m_name != item.m_shared.m_name || t.m_quality != item.m_quality || t.m_worldLevel != item.m_worldLevel || t.m_stack >= max) continue;
                int n = Mathf.Min(want, max - t.m_stack);
                if (to.MoveItemToThis(from, item, n, t.m_gridPos.x, t.m_gridPos.y)) { want -= n; moved += n; }
            }
            int guard = 0;
            while (want > 0 && M_Empty != null && guard++ < 64)
            {
                bool top = M_TopFirst != null ? (bool)M_TopFirst.Invoke(to, new object[] { item }) : true;
                Vector2i e = (Vector2i)M_Empty.Invoke(to, new object[] { top });
                if (e.x < 0 || e.y < 0) break;
                int n = Mathf.Min(want, max);
                if (!to.MoveItemToThis(from, item, n, e.x, e.y)) break;
                want -= n; moved += n;
            }
            return moved;
        }
    }

    // ---------- sort ----------
    // Tidies one container, the bag or a chest: like items are stacked together, then everything is laid out from the
    // top-left by kind (weapons, shields, tools, armor, ammo, food, materials, trophies, the rest) and by name within
    // a kind. Nothing leaves the container and nothing is lost: it is the arrangement a patient player could make by
    // hand, one move at a time. In the bag, whatever is equipped stays exactly where it is.
    static class Sorter
    {
        static readonly MethodInfo M_Changed = AccessTools.Method(typeof(Inventory), "Changed");

        static int Kind(ItemDrop.ItemData it)
        {
            switch (it.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Attach_Atgeir: return 0;
                case ItemDrop.ItemData.ItemType.Shield: return 1;
                case ItemDrop.ItemData.ItemType.Tool: return 2;
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket: return 3;
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable: return 4;
                case ItemDrop.ItemData.ItemType.Consumable: return 5;
                case ItemDrop.ItemData.ItemType.Fish: return 6;
                case ItemDrop.ItemData.ItemType.Material: return 7;
                case ItemDrop.ItemData.ItemType.Trophy: return 8;
                default: return 9;
            }
        }

        static int Compare(ItemDrop.ItemData a, ItemDrop.ItemData b)
        {
            int k = Kind(a).CompareTo(Kind(b));
            if (k != 0) return k;
            k = string.CompareOrdinal(a.m_shared.m_name, b.m_shared.m_name);
            if (k != 0) return k;
            k = b.m_quality.CompareTo(a.m_quality);
            return k != 0 ? k : b.m_stack.CompareTo(a.m_stack);
        }

        // Two piles that the game itself would let be dropped onto each other.
        static bool Same(ItemDrop.ItemData a, ItemDrop.ItemData b)
        {
            return a.m_shared.m_name == b.m_shared.m_name && a.m_quality == b.m_quality && a.m_variant == b.m_variant
                && a.m_worldLevel == b.m_worldLevel
                && (a.m_customData == null || a.m_customData.Count == 0) && (b.m_customData == null || b.m_customData.Count == 0);
        }

        public static void Run(Inventory inv, bool isBag)
        {
            if (inv == null) return;
            int width = inv.GetWidth(), height = inv.GetHeight();
            if (width <= 0 || height <= 0) return;
            System.Collections.Generic.List<ItemDrop.ItemData> items = new System.Collections.Generic.List<ItemDrop.ItemData>(inv.GetAllItems());
            bool[] taken = new bool[width * height];
            /* the bag's top row is the Controller Hotbar: left exactly as it is, items and empty slots alike, so a sort never
               changes what is on the hotbar (0.88.5) */
            bool keepTop = isBag && Favorites.TopRow;
            if (keepTop) for (int x = 0; x < width; x++) taken[x] = true;
            System.Collections.Generic.List<ItemDrop.ItemData> loose = new System.Collections.Generic.List<ItemDrop.ItemData>();
            foreach (ItemDrop.ItemData it in items)
            {
                if (it == null) continue;
                int at = it.m_gridPos.y * width + it.m_gridPos.x;
                if (isBag && it.m_equipped && at >= 0 && at < taken.Length) taken[at] = true;   // equipped: left where it is
                else if (keepTop && it.m_gridPos.y == 0) { }   /* on the hotbar row: not moved, not stacked into */
                else loose.Add(it);
            }
            // stack like with like
            for (int i = 0; i < loose.Count; i++)
            {
                ItemDrop.ItemData a = loose[i];
                int max = a.m_shared.m_maxStackSize;
                if (max <= 1 || a.m_stack <= 0) continue;
                for (int j = i + 1; j < loose.Count && a.m_stack < max; j++)
                {
                    ItemDrop.ItemData b = loose[j];
                    if (b.m_stack <= 0 || !Same(a, b)) continue;
                    int move = Mathf.Min(max - a.m_stack, b.m_stack);
                    a.m_stack += move; b.m_stack -= move;
                }
            }
            for (int i = loose.Count - 1; i >= 0; i--)
                if (loose[i].m_stack <= 0) { inv.RemoveItem(loose[i]); loose.RemoveAt(i); }   // a pile that was poured into another
            loose.Sort(Compare);
            int slot = 0;
            foreach (ItemDrop.ItemData it in loose)
            {
                while (slot < taken.Length && taken[slot]) slot++;
                if (slot >= taken.Length) break;   // cannot happen: there are never more piles than before
                it.m_gridPos = new Vector2i(slot % width, slot / width);
                slot++;
            }
            if (M_Changed != null) M_Changed.Invoke(inv, new object[] { false, false });   // the game's own "this container changed": saves a chest, updates weight
        }
    }

    // ---------- the chest's own two buttons ----------
    // The game puts "Take all" and "Place stacks" on the chest panel, each with its own shortcut (the two stick
    // clicks). On the controller both are Y now (tap fills stacks, hold takes all) and the button guide says so, so the
    // two buttons are taken out of view: they only repeated the guide. Their shortcuts are switched off too. With
    // keyboard or mouse they come back exactly as they were.
    static class ChestButtons
    {
        static UIGamePad take, stack;
        static string takeKey, stackKey;
        static CanvasGroup takeGroup, stackGroup;
        static bool applied;

        static CanvasGroup Group(Button b)
        {
            if (b == null) return null;
            CanvasGroup g = b.GetComponent<CanvasGroup>();
            return g != null ? g : b.gameObject.AddComponent<CanvasGroup>();
        }

        static void Show(CanvasGroup g, bool show)
        {
            if (g == null) return;
            g.alpha = show ? 1f : 0f; g.interactable = show; g.blocksRaycasts = show;
        }

        public static void Apply(InventoryGui gui, bool on, float ring)
        {
            if (take == null && gui.m_takeAllButton != null)
            {
                take = gui.m_takeAllButton.GetComponent<UIGamePad>(); takeKey = take != null ? take.m_zinputKey : null;
                takeGroup = Group(gui.m_takeAllButton);
            }
            if (stack == null && gui.m_stackAllButton != null)
            {
                stack = gui.m_stackAllButton.GetComponent<UIGamePad>(); stackKey = stack != null ? stack.m_zinputKey : null;
                stackGroup = Group(gui.m_stackAllButton);
            }
            if (!on)
            {
                if (!applied) return;
                applied = false;
                if (take != null) take.m_zinputKey = takeKey;
                if (stack != null) stack.m_zinputKey = stackKey;
                Show(takeGroup, true); Show(stackGroup, true);
                return;
            }
            applied = true;
            if (take != null) take.m_zinputKey = "";
            if (stack != null) stack.m_zinputKey = "";
            // faded out rather than switched off, so the game turning them on again for a chest does not bring them back
            Show(takeGroup, false); Show(stackGroup, false);
        }
    }
    // ---------- crafting hints ----------
    // A short list beside the crafting panel, on its left, stacked: what A does, how to craft several at once, and how
    // to close. It is only there while crafting is the panel in use. Each row ends with its button, next to the panel.
    static class CraftHints
    {
        // the same row height and the same gap as the stack under the minimap, from the same setting
        const float RowH = HudHints.StackRow, FromPanel = 18f;
        static float RowGap { get { return Mathf.Max(0f, Plugin.HudHintsSpacing.Value); } }
        static GameObject root;
        static readonly TMP_Text[] rows = new TMP_Text[3];

        public static void Hide() { if (root != null && root.activeSelf) root.SetActive(false); }

        public static void Show(InventoryGui gui, bool on)
        {
            if (!on || !Plugin.ShowHints.Value || gui.m_crafting == null) { Hide(); return; }
            if (root == null)
            {
                Vector2 leftBottom = new Vector2(0f, 0f);
                TMP_Text like = gui.m_weight != null ? gui.m_weight : (gui.m_armor != null ? gui.m_armor : gui.m_containerName);
                TMP_Text floating = null;
                try
                {
                    if (KeyHints.instance != null)
                        foreach (TMP_Text t in KeyHints.instance.GetComponentsInChildren<TMP_Text>(true))
                            if (t.font != null && t.font.name.Contains("AveriaSans")) { floating = t; break; }
                }
                catch (Exception) { floating = null; }
                root = Hints.New("ControllerPro_CraftHints", gui.m_crafting);
                // hangs off the panel's bottom-left corner, clear of the wood frame, level with the Craft button's end of the panel
                Hints.Place(root, leftBottom, leftBottom, new Vector2(1f, 0f), new Vector2(-(13f + FromPanel), 0f), new Vector2(10f, 2f * RowH + RowGap));
                for (int i = 0; i < 3; i++)
                {
                    rows[i] = Hints.Text(Hints.New("row" + i, root.transform), like, 16f, Color.white, TextAlignmentOptions.Right);
                    rows[i].fontStyle = FontStyles.Bold;
                    if (floating != null) { rows[i].font = floating.font; rows[i].fontSharedMaterial = floating.fontSharedMaterial; }
                    Hints.Place(rows[i].gameObject, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(0f, -i * (RowH + RowGap)), new Vector2(420f, RowH));
                }
            }
            root.SetActive(true);
            bool canCraft = gui.m_craftButton != null && gui.m_craftButton.gameObject.activeInHierarchy && gui.m_craftButton.interactable;
            // with the cursor up, A presses whatever the cursor is on
            bool cursor = Cursor.On;
            bool onCraft = !cursor || Cursor.OverThis(gui.m_craftButton);
            string a = Hints.Glyph("JoyButtonA");
            Set(0, (onCraft ? "Craft" : "Select") + "  " + a, cursor ? Cursor.OverButton && (!onCraft || canCraft) : canCraft);
            int many = 5;
            try { FieldInfo f = AccessTools.Field(typeof(InventoryGui), "m_multiCraftAmount"); if (f != null) many = (int)f.GetValue(gui); } catch (Exception) { }
            Set(1, "Craft \u00d7" + many + "  " + Hints.Glyph("JoyLStick") + " + " + a, canCraft);
            if (rows[2].gameObject.activeSelf) rows[2].gameObject.SetActive(false);   // Close is not listed: B closes, as everywhere
            // every row ends on the same edge, measured from what is drawn
            for (int i = 0; i < 2; i++)
            {
                float right;
                RectTransform rt = rows[i].rectTransform;
                float dx = HudHints.InkRight(rows[i], out right) ? Mathf.Clamp(rt.rect.xMax - right, -80f, 80f) : 0f;
                rt.anchoredPosition = new Vector2(dx, -i * (RowH + RowGap));
            }
        }

        static void Set(int i, string text, bool on)
        {
            if (rows[i].text != text) rows[i].text = text;
            rows[i].alpha = on ? 1f : 0.3f;
        }
    }

    // ---------- moving a piece of the game's UI, and a clear screen behind the inventory ----------
    // Moves a piece of the game's UI by an amount, on top of wherever the game itself puts it. If the game moves the
    // piece, that new place becomes the starting point, so the two never fight and nothing drifts.
    class Nudge
    {
        RectTransform rt;
        Vector2 applied;
        bool has;
        public Vector2 Offset;
        // how much of this nudge the target carries right now: none if something (its animator) has put it back since
        public Vector2 Present(RectTransform target)
        {
            return has && rt == target && target.anchoredPosition == applied ? Offset : Vector2.zero;
        }
        public void Set(RectTransform target, Vector2 by)
        {
            if (target == null) return;
            if (rt != target) { rt = target; has = false; Offset = Vector2.zero; }
            Vector2 now = rt.anchoredPosition;
            Vector2 start = has && now == applied ? now - Offset : now;
            Vector2 want = start + by;
            if (want != now) rt.anchoredPosition = want;
            applied = want; Offset = by; has = true;
        }
    }

    // A hold in progress, shown where the player is looking. With the cursor up, the cursor turns into the hold ring.
    // With the D-pad, the same ring fills on top of the focused slot. The Button Guide keeps its own ring as well.
    [HarmonyPatch(typeof(Hud), "StaminaBarEmptyFlash")]
    static class Patch_StaminaFlash { static void Postfix() { Vitals.Flash(0); } }
    [HarmonyPatch(typeof(Hud), "EitrBarEmptyFlash")]
    static class Patch_EitrFlash { static void Postfix() { Vitals.Flash(1); } }

    // ---------- Layout 2: the vitals grouped in the top left ----------
    // Health, stamina and eitr as bars stacked one under the other, each filling from the left edge (the game's own
    // health bar stands on its side, and its stamina and eitr bars widen from the middle of the screen). Under them the
    // food row, then the Forsaken power with its name and status beside it. Eitr only shows while the character has any.
    // It is the mod's own display, drawn with the game's art and reading the game's own values and texts; the game's
    // displays it replaces are hidden while it shows, and come straight back in the other layouts or on keyboard/mouse.
    // ---------- Text styles (0.91.8, Rob) ----------
    // Every text the mod draws uses one of these named styles, so a style looks and sizes the same wherever it appears.
    // All are the game's own fonts and looks, copied from a matching part of its UI:
    //   Title      the game's window title (copied whole with the window shell; not set here)
    //   Header     the Settings screen's yellow labels: Averia Serif Libre Bold, yellow
    //   Group      a group's name in the settings window: as Header, in the Settings screen's grey, spaced, upper case
    //   Body       the pause menu controller picture's labels: Averia Serif Libre, white (its size matched on screen)
    //   Value      the current choice inside a control: the game's own text in its copied dropdown and arrows (not set here)
    //   Hint       the game's floating hints (Attack, Block): Averia Sans Libre Bold, white, dark outline
    //   HintSmall  a hint's second line: as Hint, regular, smaller, 60% white
    //   HudNumber  counts and values on the HUD: Averia Sans Libre with the game's outline (sizes kept where they are)
    // Header, Group and Hint sizes are in the HUD's own units and kept that size on screen wherever the text sits, so the
    // same style is the same size in the settings window, the onboarding and anywhere else (until 0.91.8 the yellow header
    // was 20 in one window, 18 in another, and the windows' different scales made even equal numbers differ).
    static class TextStyle
    {
        public enum Kind { Header, Group, Body, Hint, HintSmall }
        public const float HeaderSize = 20f, GroupSize = 16f, HintSize = 16f, HintSmallSize = 12f, HintSmallAlpha = 0.6f, HudNumberSize = 13f;
        public static void Apply(TMP_Text x, Kind k)
        {
            if (x == null) return;
            switch (k)
            {
                case Kind.Header: ModSettings.LabelStyle(x); OnScreen(x, HeaderSize, 1f); break;
                case Kind.Group:
                    ModSettings.LabelStyle(x); x.color = ModSettings.Grey(); x.characterSpacing = 4f;
                    if (x.text != null) x.text = x.text.ToUpperInvariant();
                    OnScreen(x, GroupSize, GroupSize / HeaderSize); break;
                case Kind.Body: HudPick.BodyLook(x); break;
                case Kind.Hint: HintFont(x); x.fontStyle = FontStyles.Bold; x.color = Color.white; OnScreen(x, HintSize); break;
                case Kind.HintSmall: HintFont(x); x.fontStyle = FontStyles.Normal; x.color = new Color(1f, 1f, 1f, HintSmallAlpha); OnScreen(x, HintSmallSize); break;
            }
        }
        // the font of the game's floating hints (the same the button guide borrows)
        static TMP_Text hintLike;
        static void HintFont(TMP_Text x)
        {
            if (hintLike == null && KeyHints.instance != null)
                foreach (TMP_Text t in KeyHints.instance.GetComponentsInChildren<TMP_Text>(true))
                    if (t.font != null && t.font.name.Contains("AveriaSans")) { hintLike = t; break; }
            if (hintLike == null) return;
            x.font = hintLike.font; x.fontSharedMaterial = hintLike.fontSharedMaterial;
        }
        // the HUD canvas's scale: the reference every style's size is measured against
        public static float HudScale()
        {
            Hud h = Hud.instance;
            Transform c = h != null && h.m_rootObject != null ? h.m_rootObject.transform.parent : null;
            return c != null ? Mathf.Abs(c.lossyScale.y) : 0f;
        }
        static void OnScreen(TMP_Text x, float units) { OnScreen(x, units, 0f); }
        /* ofHeader > 0: sized as that share of the game's own Settings labels on screen, once measured (0.91.9) */
        static void OnScreen(TMP_Text x, float units, float ofHeader)
        {
            x.enableAutoSizing = false;
            ScreenSize s = x.GetComponent<ScreenSize>();
            if (s == null) s = x.gameObject.AddComponent<ScreenSize>();
            s.units = units; s.ofHeader = ofHeader; s.Fit();
        }
        // The Header's size is the game's own (0.91.9, Rob: 20 on the HUD read too large): its Settings screen's yellow labels,
        // measured on screen the first time that screen is open, and remembered. Until then, 20 on the HUD's scale.
        static bool measuredNow;
        public static void MeasureHeader(GameObject settings)
        {
            if (settings == null || !settings.activeInHierarchy) { measuredNow = false; return; }
            if (measuredNow) return;
            float best = 0f; string what = "";
            foreach (TMP_Text t in settings.GetComponentsInChildren<TMP_Text>(false))
            {
                Color c = t.color;
                if (!(c.r > 0.8f && c.g > 0.45f && c.g < 0.85f && c.b < 0.35f) || string.IsNullOrEmpty(t.text)) continue;
                if (t.GetComponentInParent<TMP_Dropdown>() != null) continue;
                float px = t.fontSize * Mathf.Abs(t.transform.lossyScale.y);
                if (px < 4f) continue;
                /* the most common size wins over a lone title: take the first label of the usual 14 */
                if (best == 0f || Mathf.Abs(t.fontSize - 14f) < 0.5f) { best = px; what = t.text; if (Mathf.Abs(t.fontSize - 14f) < 0.5f) break; }
            }
            if (best <= 0f) return;
            measuredNow = true;
            float hud = HudScale();
            if (hud <= 0.0001f) { measuredNow = false; return; }
            float u = best / hud;   /* in the HUD's units, so it follows the resolution (0.92.30) */
            if (Mathf.Abs(Plugin.HeaderSeen.Value - u) > 0.05f)
            {
                Plugin.HeaderSeen.Value = u;
                Plugin.Log.LogInfo("Text styles: the game's Settings labels are " + best.ToString("0.0") + " px on screen ('" + what + "'), " + u.ToString("0.00") + " HUD units; Header drawn at that size");
            }
        }
    }
    // keeps a text at its style's size on screen, also when the window it is in moves to a differently scaled canvas
    class ScreenSize : MonoBehaviour
    {
        public float units, ofHeader;
        TMP_Text t;
        public void Fit()
        {
            if (t == null) t = GetComponent<TMP_Text>();
            if (t == null) return;
            float hud = TextStyle.HudScale(), k = Mathf.Abs(transform.lossyScale.y);
            float px = ofHeader > 0f && Plugin.HeaderSeen.Value > 0f ? Plugin.HeaderSeen.Value * ofHeader * hud : units * hud;   /* both in HUD units, drawn at this resolution */
            float want = k > 0.0001f && px > 0f ? px / k : units;
            if (Mathf.Abs(t.fontSize - want) > 0.01f) t.fontSize = want;
        }
        void OnEnable() { Fit(); }
        void LateUpdate() { Fit(); }
    }
    // keeps a Body text at the size the controller picture's labels have on screen (0.91.12, Rob: 0.91.11 drew it far smaller)
    class BodySize : MonoBehaviour
    {
        TMP_Text t;
        public void Fit()
        {
            if (t == null) t = GetComponent<TMP_Text>();
            if (t == null) return;
            float px = HudPick.BodyPx(), k = Mathf.Abs(transform.lossyScale.y);
            if (px <= 0.5f || k < 0.0001f) return;
            float want = Mathf.Clamp(px / k, 6f, 60f);   /* never absurd while the canvas is still settling (0.92.15) */
            if (Mathf.Abs(t.fontSize - want) > 0.01f) { t.enableAutoSizing = false; t.fontSize = want; }
        }
        void OnEnable() { Fit(); }
        void LateUpdate() { Fit(); }
    }

    static class Vitals
    {
        // Sizes are the game's own. The health bar is 29 thick: its layout says 32, but its art leaves an empty edge, and
        // 29 is what it draws on screen (measured side by side in a screenshot). Stamina and eitr are 16. Each bar sits in a
        // frame 4 larger on every side. A food slot is 45 square with a 43 icon, 46 apart. The Forsaken power is a 64 box
        // holding a 50 background and a 50 icon. The distance from the screen edges is the minimap's own.
        const float Icon = 32f, IconGap = 10f, Inset = 4f, HealthThick = 29f, BarThick = 16f, RowGap = 3f, PowerGap = 32f,   /* the Forsaken power set apart from the bars: 32 (the game-like 16 felt cramped; Rob asked for more, twice) */ PowerName = 12f, PowerTime = 16f, FoodBox = 45f, FoodPitch = 46f, FoodIcon = 43f, GpBox = 64f, GpArt = 50f;
        class Bar { public RectTransform root, fill, trail; public TMP_Text value; public Image icon, flash, fillImg, trailImg, fastArt, slowArt; public float shown = -1f, thick, flashAt = -10f; public Ease front = new Ease(), back = new Ease(); }

        // The game blinks its stamina or eitr bar red when it runs out and something needs it; the same blink here.
        public static void Flash(int which)
        {
            Bar b = which == 0 ? stamina : eitr;
            if (b != null) b.flashAt = Time.unscaledTime;
        }

        // the game's own text look: its font material (which carries the dark outline) and its style
        static void Styled(TMP_Text t, TMP_Text like)
        {
            if (t == null || like == null) return;
            t.fontSharedMaterial = like.fontSharedMaterial;
            t.fontStyle = like.fontStyle;
        }
        // the game's own tiling, so a repeated texture repeats at the game's spacing
        static void Tiled(Image to, Image from) { if (to != null && from != null) { to.type = from.type; to.pixelsPerUnitMultiplier = from.pixelsPerUnitMultiplier; } }
        static float edgeX = 30f, edgeY = 30f;
        const float TopLeftInset = 1f;   // the minimap's own edge distance, like every corner piece (it was 1.5 until 0.66.0)
        static GameObject root;
        public static RectTransform Box { get { return root != null && root.activeSelf ? root.transform as RectTransform : null; } }
        static Bar health, stamina, eitr;
        static RectTransform foodRow, gpRow;
        static readonly Image[] foodIcons = new Image[3];
        static readonly TMP_Text[] foodTimes = new TMP_Text[3];
        static readonly GameObject[] foodBoxes = new GameObject[3];
        static Image gpIcon; static TMP_Text gpName, gpStatus;
        static bool hiding;
        // the place kept for the slim buffs and debuffs (its top left corner), or null when the group keeps none
        const float BuffBesideFood = 16f;   /* Action RPG: between the last food slot and the first buff (0.90.9) */
        const float BuffGap = 8f, BuffTopGap = RowGap;   // over the health bar, as close as the food row is under the bars (20 until 0.69.2)
        static RectTransform buffSlot; static bool buffsHere;
        public static RectTransform BuffSlot { get { return buffsHere && root != null && root.activeSelf && buffSlot != null ? buffSlot : null; } }

        static bool Wanted()
        {
            return Plugin.FavEnabled.Value && Plugin.VitalsGrouped && Plugin.LayoutOn
                && Hud.instance != null && Player.m_localPlayer != null && !Hud.IsUserHidden() && !Plugin.BagCovers && !Minimap.IsOpen();
        }

        public static void Off()
        {
            if (root != null && root.activeSelf) root.SetActive(false);
            Release();
        }

        // The game's own displays: hidden while the group shows, and put back exactly as they were when it goes. (Leaving
        // them to the game's own fades did not work: the stamina bar only fades again when stamina changes, so it stayed
        // invisible on keyboard and mouse.)
        public static bool Hiding { get { return hiding; } }
        // Is this one of the game's displays the group has hidden right now? (Then nothing else in the mod fades it.)
        public static bool HidesNow(GameObject go)
        {
            Hud h = Hud.instance;
            if (!hiding || h == null || go == null) return false;
            if (h.m_healthPanel != null ? go == h.m_healthPanel.gameObject : (h.m_healthBarRoot != null && go == h.m_healthBarRoot.gameObject)) return true;
            if (h.m_foodBarRoot != null && go == h.m_foodBarRoot.gameObject) return true;   // inside the health panel
            return barsHidden && ((h.m_staminaBar2Root != null && go == h.m_staminaBar2Root.gameObject) || (h.m_eitrBarRoot != null && go == h.m_eitrBarRoot.gameObject));
        }
        // Mixed keeps the game's stamina and eitr bars: only its health panel (health and food) is hidden then.
        static bool barsHidden;
        static void Hide(Hud h, bool bars)
        {
            hiding = true;
            SetAlpha(h.m_healthPanel, 0f);
            if (bars) { SetAlpha(h.m_staminaBar2Root, 0f); SetAlpha(h.m_eitrBarRoot, 0f); barsHidden = true; }
            else if (barsHidden) { barsHidden = false; Restart(h.m_staminaBar2Root); Restart(h.m_eitrBarRoot); }   // switched to Mixed: the game's bars come back
        }
        static void Release()
        {
            if (!hiding) return;
            hiding = false;
            Hud h = Hud.instance;
            if (h == null) return;
            // Shown fully, and the game's own show/hide logic for each is started over, so the game decides again whether
            // it should show (stamina and eitr hide while full). Putting back a value recorded earlier did not work: the
            // inventory's HUD fade records these too, and each recorded the other's half-finished fade.
            Restart(h.m_healthPanel);
            if (barsHidden) { barsHidden = false; Restart(h.m_staminaBar2Root); Restart(h.m_eitrBarRoot); }
            GiveBackPower(h);
        }
        // The Forsaken power box is the game's own, moved into the group: it looks and behaves exactly as the game's
        // (its icon, its frame, how it shows a cooldown), with its own name above it and its time under it, laid out as
        // the game lays them out. It goes back where it was when the group goes.
        static bool powerMoved;
        static Vector2 powerWasAt;
        static Transform powerName, powerTime;
        static readonly Vector3[] pc = new Vector3[4], bc = new Vector3[4];
        static void PlacePower(Hud h, RectTransform slot)
        {
            RectTransform g = h.m_gpRoot;
            if (g == null || slot == null) return;
            if (!powerMoved)
            {
                powerMoved = true; powerWasAt = g.anchoredPosition;
                powerName = g.Find("Name"); powerTime = g.Find("TimeText");
            }
            // centre the game's box on the slot kept for it
            slot.GetWorldCorners(bc); g.GetWorldCorners(pc);
            Vector3 want = (bc[0] + bc[2]) * 0.5f, now = (pc[0] + pc[2]) * 0.5f;
            if ((want - now).sqrMagnitude > 0.01f) g.position += new Vector3(want.x - now.x, want.y - now.y, 0f);
        }
        static void GiveBackPower(Hud h)
        {
            if (!powerMoved) return;
            powerMoved = false;
            if (h.m_gpRoot != null) h.m_gpRoot.anchoredPosition = powerWasAt;
            if (powerName != null) powerName.gameObject.SetActive(true);
            if (powerTime != null) powerTime.gameObject.SetActive(true);
        }

        static void Restart(RectTransform rt)
        {
            if (rt == null) return;
            SetAlpha(rt, 1f);
            Animator an = rt.GetComponent<Animator>();
            if (an != null && an.isActiveAndEnabled) { an.Rebind(); an.Update(0f); }
        }

        static void SetAlpha(RectTransform rt, float a)
        {
            CanvasGroup g = rt != null ? rt.GetComponent<CanvasGroup>() : null;
            if (g != null) g.alpha = a;
        }

        public static void Tick()
        {
            if (!Wanted()) { Off(); return; }
            Hud h = Hud.instance; Player p = Player.m_localPlayer;
            if (root == null || root.transform.parent == null) Build(h);
            if (root == null) return;
            if (!root.activeSelf) root.SetActive(true);
            bool mixed = Plugin.VitalsMixed;
            Hide(h, !mixed);
            Edges(root.transform.parent);
            // drawn at the scale the game draws its own health panel, so the game's sizes come out at the game's size on screen
            if (h.m_healthPanel != null && root.transform.parent != null)
            {
                float k = h.m_healthPanel.lossyScale.x / Mathf.Max(0.0001f, root.transform.parent.lossyScale.x);
                if (k > 0.2f && k < 5f && Mathf.Abs(root.transform.localScale.x - k) > 0.001f) root.transform.localScale = new Vector3(k, k, 1f);
            }
            Measure(h);

            // Bottom left: the same group in the bottom left corner, the Forsaken power moved to the top, set apart, and
            // the rest reading down to the screen edge. The group is held by its bottom, so it grows upward when eitr shows.
            bool bottom = Plugin.VitalsBottomLeft;
            bool gp = h.m_gpRoot != null && h.m_gpRoot.gameObject.activeSelf && h.m_gpIcon != null && h.m_gpIcon.sprite != null;
            UpdateFood(h);
            float y = 0f;
            if (bottom && gp) { gpRow.anchoredPosition = new Vector2(0f, -PowerName); y = PowerName + GpBox + PowerTime + PowerGap; }
            // the slim buffs and debuffs, when they sit in the group: a row between the bars and the Forsaken power, lined
            // up with the food slots (bottom left: under the power, over the bars)
            int nb = Buffs.UnderBars ? Buffs.Count() : 0;
            buffsHere = nb > 0;
            if (buffsHere && bottom) { buffSlot.anchoredPosition = new Vector2(Icon + IconGap, -y); y += Buffs.Height(nb) + BuffGap; }
            // Action RPG (top left): the row tops the group, floating over the health bar, 20 clear of it. It and the Forsaken
            // power line up with the bars and the food slots: only the health and food icons stand further left.
            /* Action RPG since 0.90.9 (Rob): the food row tops the group, the buffs and debuffs to its right with a small gap
               between the two, so neither reads as the other; then health and the other bars, then the Forsaken power.
               (Until 0.90.8 the buffs topped the group over the health bar, and the food row sat under the bars.) */
            if (!bottom)
            {
                foodRow.anchoredPosition = Vector2.zero;
                if (buffsHere) buffSlot.anchoredPosition = new Vector2(Icon + IconGap + 2f * FoodPitch + FoodBox + BuffBesideFood, 0f);
                y += FoodBox + RowGap;
            }
            // each bar exactly as long as the game makes its own right now: health grows with the food eaten, stamina and
            // eitr with their maximum. Close together, as the game keeps its own.
            y = Row(health, p.GetHealth(), p.GetMaxHealth(), y, true, Len(h.m_healthBarFast), h.m_healthBarFast, h.m_healthBarSlow);
            // Mixed: stamina and eitr stay the game's own bars, in their place, so the group has health, food and the power
            y = Row(stamina, p.GetStamina(), p.GetMaxStamina(), y, !mixed, Len(h.m_staminaBar2Fast), h.m_staminaBar2Fast, h.m_staminaBar2Slow);
            float maxE = p.GetMaxEitr();
            y = Row(eitr, p.GetEitr(), maxE, y, !mixed && maxE > 0.5f, Len(h.m_eitrBarFast), h.m_eitrBarFast, h.m_eitrBarSlow);

            if (bottom) { foodRow.anchoredPosition = new Vector2(0f, -y); y += FoodBox + RowGap; }

            // Forsaken power: its icon, its name and its status (Ready, or the time left), taken from the game's own
            if (gpRow.gameObject.activeSelf != gp) gpRow.gameObject.SetActive(gp);
            if (!gp) GiveBackPower(h);
            float height = y - RowGap;
            if (gp && !bottom) { gpRow.anchoredPosition = new Vector2(Icon + IconGap - (GpBox - GpArt) * 0.5f, -(y - RowGap + PowerGap + PowerName));   /* its art (50 in a 64 box) on the bars' edge */ height = y - RowGap + PowerGap + PowerName + GpBox + PowerTime; }   // set apart from the rest, as in the game
            RectTransform rr = (RectTransform)root.transform;
            Vector2 corner = bottom ? Vector2.zero : new Vector2(0f, 1f);
            if (rr.anchorMin != corner) { rr.anchorMin = rr.anchorMax = rr.pivot = corner; }
            rr.sizeDelta = new Vector2(480f, height);
            // Action RPG (top left) sits 50% further in from the left and top edges than the minimap is from its edges: at
            // the minimap's own distances it felt pressed into the corner
            rr.anchoredPosition = bottom ? new Vector2(edgeX, edgeY) : new Vector2(edgeX * TopLeftInset, -edgeY * TopLeftInset);
            if (gp)
            {
                // the game makes its icon invisible while the power is active or cooling down and shows only the time; here
                // the icon stays, a little dimmed, with the time beside it
                PlacePower(h, (RectTransform)gpIcon.transform.parent);
                if (gpName.gameObject.activeSelf) { gpName.gameObject.SetActive(false); gpStatus.gameObject.SetActive(false); }   // the game's own name and time show instead
            }
        }

        // food: the game's own icons and times, copied as they are (it already greys a food that is running out)
        static void UpdateFood(Hud h)
        {
            for (int i = 0; i < 3; i++)
            {
                Image src = h.m_foodIcons != null && i < h.m_foodIcons.Length ? h.m_foodIcons[i] : null;
                // every slot stays, empty or not: an empty one is its background alone, as in the game
                bool on = src != null && src.gameObject.activeInHierarchy && src.sprite != null;
                if (foodIcons[i].enabled != on) foodIcons[i].enabled = on;
                TMP_Text tsrc = h.m_foodTime != null && i < h.m_foodTime.Length ? h.m_foodTime[i] : null;
                string time = on && tsrc != null && tsrc.gameObject.activeInHierarchy ? tsrc.text : "";
                if (foodTimes[i].text != time) foodTimes[i].text = time;
                if (!on) continue;
                foodIcons[i].sprite = src.sprite; foodIcons[i].color = src.color;
                if (tsrc != null) foodTimes[i].color = tsrc.color;
            }
        }

        // one bar: as wide as the game makes it for that maximum (wider for more), filling from the left
        // the length of the game's own bar, or -1 when it has none to give (the eitr bar has no length while hidden)
        static float Len(GuiBar g)
        {
            RectTransform rt = g != null ? g.transform as RectTransform : null;
            float w = rt != null ? rt.rect.width : -1f;
            return float.IsNaN(w) || w < 4f ? -1f : w;
        }

        // The game's own bar easing, step for step (its GuiBar): a change waits the bar's delay, then the shown amount
        // moves toward the new one at the bar's speed, if that bar eases that way (filling or draining); otherwise it
        // jumps. Each horizontal bar has two layers, as the game's: the front copies the game's fast bar, the pale
        // trail behind it the slow one, with their own delays and speeds.
        public class Ease
        {
            public float shown = -1f, target = -1f, delay;
            public float Step(float k, GuiBar like)
            {
                bool fill = like == null || like.m_smoothFill, drain = like == null || like.m_smoothDrain;
                float speed = like != null && like.m_smoothSpeed > 0f ? like.m_smoothSpeed : 0.6f;
                if (shown < 0f) { shown = target = k; return shown; }
                if (k != target)
                {
                    if ((k < target && drain) || (k > target && fill)) delay = like != null ? like.m_changeDelay : 0f;
                    target = k;
                }
                delay -= Time.deltaTime;
                if (delay > 0f) return shown;
                if (k > shown && fill) shown = Mathf.Clamp01(Mathf.MoveTowards(shown, k, speed * Time.deltaTime));
                else if (k < shown && drain) shown = Mathf.Clamp01(Mathf.MoveTowards(shown, k, speed * Time.deltaTime));
                else shown = k;
                return shown;
            }
        }

        static float Row(Bar b, float v, float max, float y, bool show, float len, GuiBar fast, GuiBar slow)
        {
            if (b.root.gameObject.activeSelf != show) b.root.gameObject.SetActive(show);
            if (!show) return y;
            b.root.anchoredPosition = new Vector2(0f, -y);
            float frameH = b.thick + 2f * Inset;
            float width = (len > 0f ? len : Mathf.Max(32f, max)) + 2f * Inset;
            RectTransform frame = (RectTransform)b.fill.parent;
            frame.sizeDelta = new Vector2(width, frameH);
            float inner = width - 2f * Inset, k = max > 0f ? Mathf.Clamp01(v / max) : 0f;
            // both layers ease exactly as the game's own two bars do, filling and draining
            float front = b.front.Step(k, fast), back = b.back.Step(k, slow);
            b.shown = back;
            b.fill.sizeDelta = new Vector2(inner * front, b.thick);
            b.trail.sizeDelta = new Vector2(inner * back, b.thick);
            // both layers in the game's own colours, read every frame as the game may change them: the front is the game's fast
            // bar, the layer behind it the game's slow bar (the yellow that runs ahead while health regenerates, and the pale
            // trail left when it drops). Until 0.66.2 the layer behind was a fixed pale white, so the regeneration yellow never showed.
            if (b.fastArt == null) b.fastArt = BarImage(fast);
            if (b.slowArt == null) b.slowArt = BarImage(slow);
            if (b.fastArt != null && b.fillImg.color != b.fastArt.color) b.fillImg.color = b.fastArt.color;
            if (b.slowArt != null && b.trailImg.color != b.slowArt.color) b.trailImg.color = b.slowArt.color;
            // the red flash: two soft pulses over 0.8 s, fading, behind the bar (not over its number)
            float ft = Time.unscaledTime - b.flashAt;
            bool lit = ft >= 0f && ft < 0.8f;
            if (b.flash.enabled != lit) b.flash.enabled = lit;
            if (lit)
            {
                float pulse = Mathf.Sin(ft / 0.4f * Mathf.PI); pulse *= pulse;
                Color fc = b.flash.color; fc.a = 0.55f * pulse * (1f - ft / 0.8f * 0.5f); b.flash.color = fc;
            }
            string txt = Mathf.CeilToInt(v).ToString();
            if (b.value.text != txt) b.value.text = txt;
            return y + frameH + RowGap;
        }

        static void Build(Hud h)
        {
            if (h.m_rootObject == null) return;
            TMP_Text like = h.m_staminaText != null ? h.m_staminaText : h.m_healthText;
            Vector2 tl = new Vector2(0f, 1f), mid = new Vector2(0.5f, 0.5f), lm = new Vector2(0f, 0.5f);
            root = Hints.New("ControllerPro_Vitals", h.m_rootObject.transform);
            Hints.Place(root, tl, tl, tl, new Vector2(edgeX, -edgeY), new Vector2(480f, 300f));

            // each bar in its own game art: its own frame, background and fill, its own colour and its own number's size
            health = MakeBar("health", h.m_healthText != null ? h.m_healthText : like, Find(h.m_healthBarRoot, "border"), Find(h.m_healthBarRoot, "bkg"), BarImage(h.m_healthBarFast), new Color(0.78f, 0.2f, 0.16f), Find(h.m_healthPanel, "healthicon"), HealthThick);
            stamina = MakeBar("stamina", h.m_staminaText != null ? h.m_staminaText : like, Find(h.m_staminaBar2Root, "darken"), Find(h.m_staminaBar2Root, "bkg"), BarImage(h.m_staminaBar2Fast), new Color(0.95f, 0.76f, 0.2f), null, BarThick);
            eitr = MakeBar("eitr", h.m_eitrText != null ? h.m_eitrText : like, Find(h.m_eitrBarRoot, "darken"), Find(h.m_eitrBarRoot, "bkg"), BarImage(h.m_eitrBarFast), new Color(0.56f, 0.48f, 0.91f), null, BarThick);

            Image foodBg = h.m_foodIcons != null && h.m_foodIcons.Length > 0 && h.m_foodIcons[0] != null && h.m_foodIcons[0].transform.parent != null ? h.m_foodIcons[0].transform.parent.GetComponent<Image>() : null;
            GameObject fr = Hints.New("food", root.transform);
            foodRow = Hints.Place(fr, tl, tl, tl, Vector2.zero, new Vector2(400f, FoodBox));
            Image foodIconArt = Find(h.m_healthPanel, "foodicon");
            Image fi = Hints.Img(Hints.New("icon", fr.transform), foodIconArt != null ? foodIconArt.sprite : null, foodIconArt != null ? foodIconArt.color : Color.white);
            fi.preserveAspect = true; fi.enabled = fi.sprite != null;
            Hints.Place(fi.gameObject, lm, lm, lm, Vector2.zero, new Vector2(Icon, Icon));
            for (int i = 0; i < 3; i++)
            {
                GameObject box = Hints.New("food" + i, fr.transform);
                Hints.Place(box, lm, lm, lm, new Vector2(Icon + IconGap + i * FoodPitch, 0f), new Vector2(FoodBox, FoodBox));
                Image bg = Hints.Img(box, foodBg != null ? foodBg.sprite : Hints.Plate(), foodBg != null ? foodBg.color : new Color(0f, 0f, 0f, 0.6f));
                if (foodBg != null) bg.type = foodBg.type;
                foodIcons[i] = Hints.Img(Hints.New("icon", box.transform), null, Color.white);
                foodIcons[i].preserveAspect = true;
                Hints.Place(foodIcons[i].gameObject, mid, mid, mid, Vector2.zero, new Vector2(FoodIcon, FoodIcon));
                // the time as the game draws it: its own font and size, in a 25 box in the bottom right corner
                TMP_Text timeLike = h.m_foodTime != null && h.m_foodTime.Length > i && h.m_foodTime[i] != null ? h.m_foodTime[i] : like;
                foodTimes[i] = Hints.Text(Hints.New("time", box.transform), timeLike, timeLike.fontSize, Color.white, timeLike.alignment);
                Styled(foodTimes[i], timeLike);
                Vector2 br = new Vector2(1f, 0f);
                Hints.Place(foodTimes[i].gameObject, br, br, br, new Vector2(-2f, 2f), new Vector2(25f, 25f));
                foodBoxes[i] = box;
            }

            buffSlot = Hints.Place(Hints.New("buffs", root.transform), tl, tl, tl, Vector2.zero, new Vector2(10f, 10f));
            GameObject gr = Hints.New("power", root.transform);
            gpRow = Hints.Place(gr, tl, tl, tl, Vector2.zero, new Vector2(400f, GpBox));
            Image gpBg = Find(h.m_gpRoot, "Bkg");
            // the game's box: 64 across, holding its 50 background and its 50 icon, the icon over the full background
            GameObject gbox = Hints.New("box", gr.transform);
            Hints.Place(gbox, lm, lm, lm, Vector2.zero, new Vector2(GpBox, GpBox));
            Image gb = Hints.Img(Hints.New("bkg", gbox.transform), gpBg != null ? gpBg.sprite : Hints.Plate(), gpBg != null ? gpBg.color : new Color(0f, 0f, 0f, 0.6f));
            if (gpBg != null) gb.type = gpBg.type;
            Hints.Place(gb.gameObject, mid, mid, mid, Vector2.zero, new Vector2(GpArt, GpArt));
            gpIcon = Hints.Img(Hints.New("icon", gbox.transform), null, Color.white);
            gpIcon.preserveAspect = true;
            gpIcon.enabled = false; gb.enabled = false;   // only a place: the game's own box is moved onto it
            Hints.Place(gpIcon.gameObject, mid, mid, mid, Vector2.zero, new Vector2(GpArt, GpArt));
            TMP_Text nameLike = h.m_gpName != null ? h.m_gpName : like;
            // the name in white, its status (Ready, or the time left) in the game's yellow
            gpName = Hints.Text(Hints.New("name", gr.transform), nameLike, nameLike.fontSize, Color.white, TextAlignmentOptions.BottomLeft);
            Styled(gpName, nameLike);
            Hints.Place(gpName.gameObject, lm, lm, new Vector2(0f, 0f), new Vector2(GpBox + 12f, 1f), new Vector2(300f, 22f));
            TMP_Text statusLike = h.m_gpCooldown != null ? h.m_gpCooldown : like;
            gpStatus = Hints.Text(Hints.New("status", gr.transform), statusLike, statusLike.fontSize, Color.yellow, TextAlignmentOptions.TopLeft);
            Styled(gpStatus, statusLike);
            Hints.Place(gpStatus.gameObject, lm, lm, new Vector2(0f, 1f), new Vector2(GpBox + 12f, -1f), new Vector2(300f, 22f));
        }

        static Bar MakeBar(string name, TMP_Text like, Image frameArt, Image bkgArt, Image fillArt, Color fallback, Image iconArt, float thick)
        {
            Vector2 tl = new Vector2(0f, 1f), lm = new Vector2(0f, 0.5f);
            Bar b = new Bar();
            b.thick = thick;
            float BarH = thick + 2f * Inset;
            GameObject r = Hints.New(name, root.transform);
            b.root = Hints.Place(r, tl, tl, tl, Vector2.zero, new Vector2(480f, BarH));
            b.icon = Hints.Img(Hints.New("icon", r.transform), iconArt != null ? iconArt.sprite : Hints.Disc(), iconArt != null ? iconArt.color : fallback);
            b.icon.preserveAspect = true;
            Hints.Place(b.icon.gameObject, lm, lm, lm, Vector2.zero, iconArt != null ? new Vector2(Icon, Icon) : new Vector2(10f, 10f));
            if (iconArt == null) ((RectTransform)b.icon.transform).anchoredPosition = new Vector2((Icon - 10f) * 0.5f, 0f);
            GameObject frame = Hints.New("frame", r.transform);
            Hints.Place(frame, lm, lm, lm, new Vector2(Icon + IconGap, 0f), new Vector2(200f, BarH));
            Image fr = Hints.Img(frame, frameArt != null ? frameArt.sprite : Hints.Plate(), frameArt != null ? frameArt.color : new Color(0f, 0f, 0f, 0.7f));
            Tiled(fr, frameArt);
            if (bkgArt != null)
            {
                Image bk = Hints.Img(Hints.New("bkg", frame.transform), bkgArt.sprite, bkgArt.color);
                Tiled(bk, bkgArt);
                Hints.Place(bk.gameObject, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-2f * Inset, -2f * Inset));
            }
            Color col = fillArt != null ? fillArt.color : fallback;
            Image trail = Hints.Img(Hints.New("trail", frame.transform), fillArt != null ? fillArt.sprite : null, new Color(1f, 1f, 1f, 0.45f));
            b.trailImg = trail;
            Tiled(trail, fillArt);
            b.trail = Hints.Place(trail.gameObject, lm, lm, lm, new Vector2(Inset, 0f), new Vector2(0f, thick));
            Image fill = Hints.Img(Hints.New("fill", frame.transform), fillArt != null ? fillArt.sprite : null, col);
            b.fillImg = fill;
            Tiled(fill, fillArt);
            b.fill = Hints.Place(fill.gameObject, lm, lm, lm, new Vector2(Inset, 0f), new Vector2(0f, thick));
            b.value = Hints.Text(Hints.New("value", frame.transform), like, like.fontSize, like.color, TextAlignmentOptions.MidlineLeft);
            Styled(b.value, like);
            b.flash = Hints.Img(Hints.New("flash", frame.transform), frameArt != null ? frameArt.sprite : Hints.Plate(), new Color(0.9f, 0.12f, 0.08f, 0.75f));
            Tiled(b.flash, frameArt);
            Hints.Place(b.flash.gameObject, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            b.flash.enabled = false;
            b.flash.transform.SetSiblingIndex(bkgArt != null ? 1 : 0);   // just above the bar's background, under its fill and number
            Hints.Place(b.value.gameObject, Vector2.zero, Vector2.one, lm, new Vector2(8f, 1f), new Vector2(-8f, 0f));
            return b;
        }

        // Once, two seconds after the group first shows: its sizes on screen beside the game's own, to the log.
        static float shownAt = -1f; static bool measured;
        static readonly Vector3[] q = new Vector3[4];
        static string Px(RectTransform rt) { if (rt == null) return "-"; rt.GetWorldCorners(q); return Mathf.Abs(q[2].x - q[0].x).ToString("0") + "x" + Mathf.Abs(q[2].y - q[0].y).ToString("0"); }
        static void Measure(Hud h)
        {
            if (measured) return;
            if (shownAt < 0f) { shownAt = Time.unscaledTime; return; }
            if (Time.unscaledTime - shownAt < 2f) return;
            measured = true;
            RectTransform food0 = h.m_foodIcons != null && h.m_foodIcons.Length > 0 && h.m_foodIcons[0] != null ? h.m_foodIcons[0].transform.parent as RectTransform : null;
            Image hi = Find(h.m_healthPanel, "healthicon");
            Plugin.Log.LogInfo("Vitals measured on screen (px): health frame ours " + Px((RectTransform)health.fill.parent) + " game " + Px(h.m_healthBarRoot)
                + " | stamina frame ours " + Px((RectTransform)stamina.fill.parent) + " game " + Px(h.m_staminaBar2Root)
                + " | food slot ours " + Px((RectTransform)foodBoxes[0].transform) + " game " + Px(food0)
                + " | health icon ours " + Px((RectTransform)health.icon.transform) + " game " + Px(hi != null ? hi.rectTransform : null)
                + " | power box ours " + Px((RectTransform)gpIcon.transform.parent) + " game " + Px(h.m_gpRoot)
                + " | scale ours " + root.transform.lossyScale.x.ToString("0.000") + " game panel " + (h.m_healthPanel != null ? h.m_healthPanel.lossyScale.x.ToString("0.000") : "-")
                + " | health rotation " + (h.m_healthBarRoot != null ? h.m_healthBarRoot.localEulerAngles.ToString() : "-")
                + " | health fast " + BarLog(h.m_healthBarFast) + " slow " + BarLog(h.m_healthBarSlow));
        }

        // the minimap's own distance from the top and right edges of the screen, used for the top and left of the group
        static readonly Vector3[] mc = new Vector3[4];
        static void Edges(Transform canvasSpace)
        {
            if (canvasSpace == null) return;
            float s = Mathf.Max(0.0001f, canvasSpace.lossyScale.x);
            // Bottom HUD: the map picture's own distances, so the group shares the hotbar's and the map's bottom line (0.73.0)
            float right, top;
            if (Plugin.VitalsBottomLeft)
            {
                if (MapPlace.PicGaps(out right, out top)) { edgeX = right / s; edgeY = top / s; }
                return;
            }
            // Action RPG has its own layout and keeps the place it had before 0.73.0: the map frame's distances (Rob, 0.74.2)
            Minimap mm = Minimap.instance;
            RectTransform small = mm != null && mm.m_smallRoot != null ? mm.m_smallRoot.transform as RectTransform : null;
            if (small == null || !small.gameObject.activeInHierarchy) return;
            small.GetWorldCorners(mc);
            right = (Screen.width - mc[2].x) / s; top = (Screen.height - (mc[2].y - MapPlace.ShiftY)) / s;   // where the game has the map, even while it is moved down
            if (right >= 0f && right < 400f) edgeX = right;
            if (top >= 0f && top < 400f) edgeY = top;
        }

        static string BarLog(GuiBar g) { Image im = BarImage(g); return g == null ? "-" : "colour " + (im != null ? im.color.ToString() : "-") + " fill " + g.m_smoothFill + " drain " + g.m_smoothDrain + " speed " + g.m_smoothSpeed + " delay " + g.m_changeDelay; }
        static Image BarImage(GuiBar g) { return g != null && g.m_bar != null ? g.m_bar.GetComponent<Image>() : null; }
        static Image Find(RectTransform under, string name)
        {
            if (under == null) return null;
            foreach (Image im in under.GetComponentsInChildren<Image>(true)) if (im.name == name) return im;
            return null;
        }
    }

    // ---------- Controller Support Plus Settings: a window in the game ----------
    // Opened from the pause menu's button (or Select there), or with LT + Select in play when that setting is on. The
    // settings sit in tabs (LB and RB), one row each: the name on the left and, on the right, a copy of one of the game's
    // own Settings screen controls (its two arrows, its checkbox, or a game button). Up and down pick a row, left and right
    // step its value, A ticks a checkbox or presses a button, B or Start closes. Every change is made at once on the same
    // setting as in the mod manager, with the HUD in view behind the window. If a game control cannot be found, a plain
    // "< value >" is drawn instead.
    static class ModSettings
    {
        public static bool Open;
        static int closedFrame = -1, seenFrame = -1, focus, openedFrame = -1;
        static bool overMenu, overWorld;
        public static bool LtSelectOn { get { return Plugin.LtSelectSettings.Value && Plugin.Enabled.Value && ZInput.IsGamepadActive(); } }
        /* LT is held with the setting on: Select then opens the window, so neither the map nor the chat takes it (1.0.0) */
        public static bool LtSelectChord { get { return LtSelectOn && Core.RawHeld("JoyLTrigger"); } }
        static GameObject root;
        static RectTransform panel;
        public static bool Blocking { get { return Open || Time.frameCount == closedFrame || releaseWait || HudPick.Blocking; } }   /* the first-launch HUD pick holds the buttons the same way */
        /* the window steps aside while the HUD pick it opened is up, and comes back after (0.91.0) */
        public static void HideForPick(bool hide) { if (root != null && Open) { root.SetActive(!hide); if (!hide) Refresh(); } }
        public static void LabelStyle(TMP_Text x) { LabelLook(x); }
        public static Color Grey() { return GreyLook(); }
        public static Color Yellow() { if (!labelLooked) { TMP_Text probe = null; LabelLook(probe); } return labelColour; }
        /* after closing: the button that closed it (B, Start, A) stays the window's until it is let go, so it does nothing
           in play as it comes back up (B's dodge happens on its release) */
        static bool releaseWait;
        static void ReleaseTick() { if (releaseWait && !Core.RawHeld("JoyButtonB") && !Core.RawHeld("JoyMenu") && !Core.RawHeld("JoyButtonA") && !Core.RawHeld("JoyDodge")) releaseWait = false; }

        class Row
        {
            public string name; public string[] shown; public Func<int> index; public Action<int> set; public bool stepper, check, header; public Func<string> named; public Toggle box;
            /* a value beside a checkbox (1.0.2, the LT + D-pad layer): in the arrows' value text, the Settings screen's grey while sideOff */
            public Func<string> sideText; public Func<bool> sideOff; public TMP_Text side;
            public int tab;   /* the tab it shows on (0.92.21): the groups are tabs now, the rows before the first group are General */
            public Action act;   /* a button row: A runs it (0.91.0, Choose HUD layout) */
            public TMP_Text label, value; public Image plate;
            public List<Lit> lits = new List<Lit>();   /* the row's control(s), lit as the game lights its focused one (0.90.5) */
        }
        // A copied control's own focus look, as the Settings screen shows it on the control in focus (Rob, 0.90.5): its
        // colour tint, its swapped art or its animation, noted before the copy is cut loose and then switched by hand, so
        // only the window decides which control looks focused. Before, a soft light lit the whole row.
        class Lit
        {
            Selectable s; Selectable.Transition t; ColorBlock cb; SpriteState ss; AnimationTriggers at; Graphic g; int state = -1;
            static int logged;
            public static Lit Of(Selectable sel)
            {
                Lit l = new Lit { s = sel, t = sel.transition, cb = sel.colors, ss = sel.spriteState, at = sel.animationTriggers, g = sel.targetGraphic };
                sel.transition = Selectable.Transition.None;
                if (logged < 4) { logged++; Plugin.Log.LogInfo("Settings window: focus look of " + sel.name + " (" + sel.GetType().Name + "): " + l.t + (l.t == Selectable.Transition.ColorTint ? " normal " + l.cb.normalColor + " selected " + l.cb.selectedColor + " highlighted " + l.cb.highlightedColor + " x" + l.cb.colorMultiplier : l.t == Selectable.Transition.SpriteSwap ? " selected " + (l.ss.selectedSprite != null ? l.ss.selectedSprite.name : "-") + " highlighted " + (l.ss.highlightedSprite != null ? l.ss.highlightedSprite.name : "-") : "") + ", on " + (l.g != null ? l.g.name : "nothing")); }
                l.Set(false);
                return l;
            }
            public bool Works
            {
                get
                {
                    if (t == Selectable.Transition.ColorTint) return g != null && (cb.selectedColor != cb.normalColor || cb.highlightedColor != cb.normalColor);
                    if (t == Selectable.Transition.SpriteSwap) return g is Image && (ss.selectedSprite != null || ss.highlightedSprite != null);
                    if (t == Selectable.Transition.Animation) return s != null && s.animator != null;
                    return false;
                }
            }
            public void Set(bool on)
            {
                int want = on ? 1 : 0;
                if (want == state) return;
                state = want;
                if (t == Selectable.Transition.ColorTint && g != null)
                {
                    Color sel = cb.selectedColor != cb.normalColor ? cb.selectedColor : cb.highlightedColor;
                    g.CrossFadeColor((on ? sel : cb.normalColor) * cb.colorMultiplier, 0f, true, true);
                }
                else if (t == Selectable.Transition.SpriteSwap && g is Image) ((Image)g).overrideSprite = on ? (ss.selectedSprite != null ? ss.selectedSprite : ss.highlightedSprite) : null;
                else if (t == Selectable.Transition.Animation && s != null && s.animator != null && s.animator.isActiveAndEnabled)
                {
                    s.animator.ResetTrigger(at.normalTrigger); s.animator.ResetTrigger(at.selectedTrigger); s.animator.ResetTrigger(at.highlightedTrigger);
                    s.animator.SetTrigger(on ? (string.IsNullOrEmpty(at.selectedTrigger) ? at.highlightedTrigger : at.selectedTrigger) : at.normalTrigger);
                }
            }
        }

        // A setting in the window is one line in the list below: Choice (values, each with the name shown) or Toggle
        // (Off / On). Each is the same setting as in the mod manager, so its own change handling runs (a new preset's
        // starting choices, for one). The window grows with the list.
        static Row Choice(string name, ConfigEntry<string> entry, string[] values, string[] shown)
        {
            return new Row { name = name, shown = shown, index = delegate { return Math.Max(0, Array.IndexOf(values, entry.Value)); }, set = delegate(int i) { entry.Value = values[i]; } };
        }
        static Row Toggle(string name, ConfigEntry<bool> entry)
        {
            return new Row { name = name, shown = new[] { "Off", "On" }, index = delegate { return entry.Value ? 1 : 0; }, set = delegate(int i) { entry.Value = i == 1; } };
        }
        // A change to this row also shows a sample pickup, through the game's own pickup message, so the style just
        // picked is seen at once (the mod's feed in Modern, the game's own message in Vanilla). Nothing is added to the bag.
        static Row Sample(Row r)
        {
            Action<int> set = r.set;
            r.set = delegate(int i) { set(i); ShowSample(); };
            return r;
        }
        static readonly MethodInfo M_ShowPickup = AccessTools.Method(typeof(Character), "ShowPickupMessage");
        static void ShowSample()
        {
            Player p = Player.m_localPlayer;
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("Wood") : null;
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (p == null || drop == null || M_ShowPickup == null) return;
            try { M_ShowPickup.Invoke(p, new object[] { drop.m_itemData, 1 }); }
            catch (Exception e) { Plugin.Log.LogWarning("Settings window: sample pickup: " + e.Message); }
        }

        // an On / Off setting shown as the game's own checkbox (the Settings screen's, VSync's); A ticks it
        static Row Check(string name, ConfigEntry<bool> entry) { Row r = Toggle(name, entry); r.check = true; return r; }

        // the row shown on the game's dropdown instead of between two arrows
        /* a name written fresh each time the window shows (the buttons' glyphs, for whichever controller is in use) */
        static Row Named(Row r, Func<string> n) { r.named = n; return r; }
        static Row Side(Row r, Func<string> text, Func<bool> off) { r.sideText = text; r.sideOff = off; return r; }
        /* a group's name with a thin line under it: never focused, only read */
        static Row Group(string name) { return new Row { name = name, header = true, shown = new string[0] }; }
        static Row[] rowList;
        static Row[] rows
        {
            get
            {
                if (rowList == null) rowList = new Row[]
                {
                    /* the setup again, at the very top (0.92.20, Rob; "Choose HUD layout" before): from the hotbar pick, then on
                       into the HUD layout pick, the splash skipped */
                    new Row { name = "Rerun setup", shown = new[] { "Start" }, index = delegate { return 0; }, set = delegate(int i) { }, act = HudPick.OpenFromSettings },
                    /* Reset mod (dev) left the list (0.92.21, Rob): a Select button in the screen's top left corner */
                    /* between two arrows like the rest (0.92.20, Rob; the game's dropdown before) */
                    Choice("HUD preset", Plugin.HudPreset, new[] { "Vanilla", "BottomHUD", "ActionRPG" }, new[] { "Vanilla", "Bottom HUD", "Action RPG" }),
                    /* the controller cursor (1.0.1, Rob; a checkbox since 1.0.4), then LT + Select (moved here from the last tab, 1.0.4) */
                    Check("Controller cursor", Plugin.CursorEnabled),
                    Named(Check("LT + Select opens Controller Support Plus Settings", Plugin.LtSelectSettings), delegate { return Hints.Glyph("JoyLTrigger") + " + " + Hints.Glyph("JoyMap") + "  opens Controller Support Plus Settings"; }),
                    /* alone at the top, no group: it changes everything below it */
                    /* the hotbar's own group, first under the preset (0.90.4, Rob) */
                    Group("Hotbar"),
                    // the rest: the value between two arrows, stepped with left and right
                    Choice("Hotbar layout", Plugin.HotbarStyle, new[] { "Cross", "CompactCross", "Row" }, new[] { "Cross", "Compact cross", "Row" }),
                    /* 1.0.2 (Rob): what the D-pad alone does, between two arrows, Item shortcuts first (the default); LT + D-pad
                       holds the other set. The same saved setting as the "LT + D-pad function" selector before it (0.92.20-1.0.1),
                       read the other way round, so nobody's D-pad changes */
                    Named(new Row { name = "D-pad shortcuts", shown = new[] { "Item shortcuts", "Function shortcuts" }, index = delegate { return Plugin.SwapDpadActions.Value ? 1 : 0; }, set = delegate(int i) { Plugin.SwapDpadActions.Value = i == 1; } },
                          delegate { string d = Hints.DpadGlyph(); if (string.IsNullOrEmpty(d)) d = "D-pad"; return d + " shortcuts"; }),
                    /* the LT + D-pad layer on or off (1.0.1, Rob; 1.0.2: its name stays as it is, yellow): beside the box, in the
                       arrows' value text, the set LT holds (the other one from the row above), or a grey "Off" while unticked */
                    Side(Named(Check("LT + D-pad shortcuts", Plugin.LtDpadLayer),
                          delegate { string d = Hints.DpadGlyph(); if (string.IsNullOrEmpty(d)) d = "D-pad"; return Hints.Glyph("JoyLTrigger") + " + " + d + " shortcuts"; }),
                          delegate { return !Plugin.LtDpadLayer.Value ? "Off" : Plugin.SwapDpadActions.Value ? "Item shortcuts" : "Function shortcuts"; },
                          delegate { return !Plugin.LtDpadLayer.Value; }),
                    Group("HUD elements"),
                    Check("Show button hints", Plugin.ShowMapHints),   /* a checkbox since 0.90.4 (On/Off arrows, "Button hints", before) */
                    /* global: kept as they are when the preset changes */
                    Choice("Buffs and debuffs style", Plugin.BuffStyle, new[] { "Vanilla", "Detailed", "Slim" }, new[] { "Vanilla", "Detailed", "Slim" }),
                    Sample(Choice("Pick up notification style", Plugin.FeedPos, new[] { "New", "Vanilla" }, new[] { "Modern", "Vanilla" })),
                    Choice("Equip progress bar style", Plugin.EquipProgress, new[] { "Ring", "Vanilla" }, new[] { "Radial progress", "Vanilla" }),
                    /* "Misc" (1.0.4, Rob; "Input and compatibility" before) */
                    Group("Misc"),
                    /* the game's auto-pickup (1.0.1; a checkbox here since 1.0.4): its block + left stick chord is off on the Controller Support Plus layout */
                    Check("Auto-pickup", Plugin.AutoPickup),
                    Check("Keep HUD layout while using keyboard and mouse", Plugin.HudOnKbm),   // last: it changes where the rest applies
                };
                if (tabNames == null)
                {
                    /* the tabs (0.92.21, Rob): General, then one per group, in order */
                    List<string> names = new List<string> { "General" };
                    foreach (Row r in rowList) { if (r.header) names.Add(r.name); r.tab = names.Count - 1; }
                    tabNames = names.ToArray();
                }
                return rowList;
            }
        }
        static string[] tabNames; static int tabNow;
        // Tabs (0.92.21, Rob): the tab buttons in a framed strip at the top (the crafting panel's own tab buttons, LB and RB
        // on its two ends), the rows of the tab in a framed box under it. Both frames are the onboarding cards' yellow frame
        // over a light black.
        /* 0.92.24 (Rob): no strip box; the tab buttons stand on the content box's top edge, as the crafting panel's do, at
           that tab button's own height (measured when the window is built) */
        /* 0.92.25 (Rob): the tabs as the crafting panel draws them on screen, their text at its size; the window's edge air
           smaller (14, 24 before), and the window wider when the tabs need it, so they never reach past the box under them */
        const float RowPitch = 52f, RowH = 44f, FrameX = 14f, TabsTop = 56f, ContentPad = 16f, BaseW = 680f, GlyphW = 36f, TabGap = 4f;
        static float TabsH = 46f, WinW = BaseW, tabK = 1f, tabFont = 0f;
        static float[] tabW;   // each tab button's own width, in the crafting panel's units
        static float ContentTop { get { return TabsTop + TabsH; } }
        static float BoxW { get { return WinW - 2f * FrameX; } }
        static TMP_Text TabLabelOf(Button b) { return b != null ? b.GetComponentInChildren<TMP_Text>(true) : null; }
        static void MeasureTabs(InventoryGui gui)
        {
            Button src = gui.m_tabCraft != null ? gui.m_tabCraft : gui.m_tabUpgrade;
            if (rows == null) return;   /* the list first: it names the tabs */
            tabW = new float[tabNames.Length];
            WinW = BaseW;
            if (src == null) return;
            RectTransform srt = (RectTransform)src.transform;
            // the crafting panel's scale against the HUD's canvas, where the window is shown: the tab drawn the same size
            Hud hud = Hud.instance;
            Transform hudCanvas = hud != null && hud.m_rootObject != null ? hud.m_rootObject.transform.parent : null;
            float a = srt.lossyScale.x, b = hudCanvas != null ? hudCanvas.lossyScale.x : 0f;
            tabK = a > 0.0001f && b > 0.0001f ? Mathf.Clamp(a / b, 0.5f, 3f) : 1f;
            float sw = srt.rect.width, sh = srt.rect.height;
            if (sh >= 16f && sh <= 120f) TabsH = sh * tabK;
            TMP_Text sl = TabLabelOf(src);
            tabFont = sl != null ? (sl.enableAutoSizing && sl.textInfo != null && sl.textInfo.characterCount > 0 ? sl.fontSize : sl.enableAutoSizing ? sl.fontSizeMax : sl.fontSize) : 0f;
            float own = sl != null ? sl.GetPreferredValues(sl.text).x : 0f;
            float pad = own > 1f ? Mathf.Clamp((sw - own) * 0.5f, 14f, 40f) : 22f;   /* the air the game leaves round "CRAFT" */
            float total = 2f * GlyphW + 2f * TabGap;
            for (int t = 0; t < tabNames.Length; t++)
            {
                float tw = sl != null ? sl.GetPreferredValues(tabNames[t].ToUpperInvariant()).x : 100f;
                tabW[t] = Mathf.Max(sw, tw + 2f * pad);
                total += tabW[t] * tabK + (t > 0 ? TabGap : 0f);
            }
            WinW = Mathf.Max(BaseW, Mathf.Ceil(total + 2f * FrameX));
            Plugin.Log.LogInfo("Settings window: tabs as the crafting tab '" + src.name + "' (" + sw + " x " + sh + ", text " + (sl != null ? sl.fontSize + (sl.enableAutoSizing ? " auto " + sl.fontSizeMin + "-" + sl.fontSizeMax : "") : "none") + ") at " + tabK + "x; row " + total + " wide, window " + WinW);
        }
        static int TabRows(int t) { int n = 0; foreach (Row r in rows) if (!r.header && r.tab == t) n++; return n; }
        static float ContentH { get { if (tabNames == null && rows == null) return 0f; /* the list (and with it the tab names) set up first: 0.92.22 read the names before they existed and the window never opened */ int most = 1; for (int t = 0; t < tabNames.Length; t++) most = Math.Max(most, TabRows(t)); return 2f * ContentPad + most * RowPitch - (RowPitch - RowH); } }
        /* each row's top, down from the window's top: its place among its own tab's rows */
        static float RowY(int i)
        {
            int k = 0;
            for (int j = 0; j < i; j++) if (!rows[j].header && rows[j].tab == rows[i].tab) k++;
            return -(ContentTop + ContentPad + k * RowPitch);
        }
        /* the next setting from i, d rows on, round its own tab's rows */
        static int NextRow(int i, int d)
        {
            int t = i >= 0 && i < rows.Length ? rows[i].tab : tabNow;
            for (int n = 0; n < rows.Length; n++) { i = (i + d + rows.Length) % rows.Length; if (!rows[i].header && rows[i].tab == t) return i; }
            return i;
        }
        static int FirstRow(int t) { for (int i = 0; i < rows.Length; i++) if (!rows[i].header && rows[i].tab == t) return i; return 0; }
        static void GoTab(int t)
        {
            tabNow = (t + tabNames.Length) % tabNames.Length;
            focus = FirstRow(tabNow);
            Refresh();
        }

        // Called by the inventory every frame it can take Start. True while the window has the buttons (and on the frame
        // it closes, so the B that closed it does not also close the bag).
        public static bool Frame(InventoryGui gui, bool free)
        {
            seenFrame = Time.frameCount;
            if (overMenu || overWorld) return Open;
            bool was = Open;
            /* the bag's hint is gone (0.83.0): the window's button sits under the pause menu's controller picture. Start in the
               bag still opens it. */
            /* Start in the bag no longer opens it (0.85.0): the pause menu's button, Select there, or LT + Select do */
            if (Open) Drive();
            return Open || was;
        }

        /* the open window's buttons, read once a frame */
        static void Drive()
        {
            if (HudPick.Open) return;   /* the HUD pick opened from here has the buttons */
            if (Time.frameCount == openedFrame) return;   /* the A that opened it from the menu's picture is not also a press in it (1.0.0) */
            {
                int d = Core.RawDown("JoyDPadLeft") || Core.RawDown("JoyLStickLeft") ? -1 : Core.RawDown("JoyDPadRight") || Core.RawDown("JoyLStickRight") ? 1 : 0;
                int v = Core.RawDown("JoyDPadUp") || Core.RawDown("JoyLStickUp") ? -1 : Core.RawDown("JoyDPadDown") || Core.RawDown("JoyLStickDown") ? 1 : 0;
                bool a = Core.RawDown("JoyButtonA"), b = Core.RawDown("JoyButtonB"), start = Core.RawDown("JoyMenu");
                Row r = rows[focus];
                if (b || start) Close();
                /* LB and RB step the tabs, as on the crafting panel (0.92.21) */
                else if (Core.RawDown("JoyTabLeft") || Core.RawDown("JoyLBumper")) GoTab(tabNow - 1);
                else if (Core.RawDown("JoyTabRight") || Core.RawDown("JoyRBumper")) GoTab(tabNow + 1);
#if DEV
                /* Select: the corner's Reset mod (dev) (0.92.21), never on the frame the window opened (Select opens it) */
                else if (Time.frameCount > openedFrame + 1 && Core.RawDown("JoyMap")) { HudPick.DevReset(); return; }
#endif
                else if (a && r.check) Set(r, 1 - r.index());
                else if (a && r.act != null) r.act();
                else if (d != 0 && r.act == null) Set(r, (r.index() + d + r.shown.Length) % r.shown.Length);
                else if (v != 0) { focus = NextRow(focus, v); Refresh(); }
            }
            if (Open) { Shots.Want("settings-window"); Report(); }
        }

        static void Set(Row r, int i) { r.set(i); Refresh(); }
        /* the dropdown rows went with the HUD preset's arrows (0.92.20); their list handling was taken out for V1 */

        /* closes the window and, opened over the pause menu, that menu too: straight back to play (1.0.4, finishing Rerun setup) */
        public static void CloseToPlay()
        {
            bool menu = overMenu;
            Close();
            if (menu && Menu.instance != null && Menu.IsVisible()) Menu.instance.Hide();
        }

        public static void Close()
        {
            if (Open) { closedFrame = Time.frameCount; releaseWait = true; }
            Open = false;
            if (root != null && root.activeSelf) root.SetActive(false);
            Uncover();
            if (overMenu) { overMenu = false; MenuLock(false); }
            overWorld = false;
        }

        public static void LateTick()
        {
            ReleaseTick();
            if (overMenu)
            {
                /* opened from the pause menu: open while that menu is, the menu out of sight meanwhile */
                if (!(Plugin.Enabled.Value && ZInput.IsGamepadActive() && Menu.IsVisible())) Close();
                else MenuHide();
            }
            else if (overWorld)
            {
                /* opened in play with LT + Select: open while nothing else takes the screen */
                if (!(Plugin.Enabled.Value && ZInput.IsGamepadActive() && Player.m_localPlayer != null && !Menu.IsVisible() && !InventoryGui.IsVisible() && !Minimap.IsOpen())) Close();
            }
            else
            {
                bool pad = Plugin.Enabled.Value && ZInput.IsGamepadActive() && InventoryGui.IsVisible();
                if (!pad || seenFrame < Time.frameCount - 1) { Close(); }
                if (Open) Cover();
            }
        }

        // every row's value, and the focused row lit (the others dimmed)
        static void Refresh()
        {
            ShowTab();
            for (int i = 0; i < rows.Length; i++)
            {
                Row r = rows[i];
                if (r.header) continue;
                if (r.named != null && r.label != null) { string nm = r.named(); if (r.label.text != nm) r.label.text = nm; }
                bool on = i == focus;
                int at = r.index();
                if (r.box != null && r.box.isOn != (at == 1)) r.box.isOn = at == 1;
                if (r.value != null) r.value.text = r.stepper ? r.shown[at] : "<    " + r.shown[at] + "    >";
                if (r.label != null && r.label.color != labelColour) r.label.color = labelColour;   // always the Settings screen's full yellow
                if (r.side != null && r.sideText != null)
                {
                    string st = r.sideText(); if (r.side.text != st) r.side.text = st;
                    Color sc = r.sideOff != null && r.sideOff() ? GreyLook() : Color.white;   /* white as the arrows' value; grey when off */
                    if (r.side.color != sc) r.side.color = sc;
                }
                /* the focus on the control itself, as the Settings screen shows it (0.90.5): the button, the checkbox, both
                   arrows. The row's soft light and the dimmed values of the other rows only where a control has no look of its own. */
                bool looks = false;
                foreach (Lit l in r.lits) { l.Set(on); if (l.Works) looks = true; }
                if (r.value != null) r.value.color = on || looks ? Color.white : new Color(1f, 1f, 1f, 0.55f);
                if (r.plate != null) r.plate.enabled = on && !looks;
            }
        }

        // ----- the HUD in view while the window is up -----
        // The window belongs with the HUD it changes, wherever it is opened from: while it is up, the screen it was opened
        // over (the inventory and crafting panels) is hidden and the whole HUD shows behind it with no dim, so every change
        // is seen as it is made. Closing it brings that screen back. The window sits on the HUD's own canvas for this.
        static CanvasGroup covered; static float coveredWas = 1f; static bool covering;
        static void Cover()
        {
            InventoryGui gui = InventoryGui.instance;
            Transform inv = gui != null ? gui.m_inventoryRoot : null;
            if (inv == null) return;
            if (covered == null || covered.transform != inv)
            {
                covered = inv.GetComponent<CanvasGroup>();
                if (covered == null) covered = inv.gameObject.AddComponent<CanvasGroup>();
            }
            if (!covering) { coveringWasSet(); }
            // every frame, after the game's own animation of it: hidden, and not catching the cursor
            covered.alpha = 0f; covered.blocksRaycasts = false;
        }
        static void coveringWasSet() { covering = true; coveredWas = covered.alpha; }
        static void Uncover()
        {
            if (!covering) return;
            covering = false;
            if (covered != null) { covered.alpha = coveredWas > 0.01f ? coveredWas : 1f; covered.blocksRaycasts = true; }
        }
        // the window moved onto the HUD's canvas, its dim left out (the HUD is what is to be seen)
        static void OntoHud()
        {
            Hud h = Hud.instance;
            Transform to = h != null && h.m_rootObject != null ? h.m_rootObject.transform.parent : null;
            if (to == null || root.transform.parent == to) return;
            root.transform.SetParent(to, false);
            RectTransform rr = (RectTransform)root.transform;
            rr.anchorMin = Vector2.zero; rr.anchorMax = Vector2.one; rr.pivot = new Vector2(0.5f, 0.5f); rr.anchoredPosition = Vector2.zero; rr.sizeDelta = Vector2.zero;
            Dim(false);
            Plugin.Log.LogInfo("Settings window: on the HUD's canvas '" + to.name + "', the inventory hidden while it is up");
        }

        /* the window's own dim behind it: off over the HUD (the HUD is what is to be seen), on over the pause menu */
        static void Dim(bool on)
        {
            foreach (Transform ch in root.transform) if (ch != panel && ch.name.ToLowerInvariant().Contains("darken")) ch.gameObject.SetActive(on);
            Image own = root.GetComponent<Image>(); if (own != null) own.enabled = on;
        }
        /* the whole pause menu out of sight (and its dim), every frame while the window is up: the HUD shows behind the
           window as from the bag, so every change is seen as it is made. Closing the window brings the menu back. */
        static CanvasGroup menuShade; static float menuShadeWas = 1f; static bool menuHidden;
        static void MenuHide()
        {
            Menu m = Menu.instance;
            Transform t = m != null ? (m.m_root != null ? m.m_root : m.m_menuDialog) : null;
            if (t == null) return;
            if (root != null && root.transform.IsChildOf(t)) return;
            if (menuShade == null || menuShade.transform != t)
            {
                menuShade = t.GetComponent<CanvasGroup>();
                if (menuShade == null) menuShade = t.gameObject.AddComponent<CanvasGroup>();
                menuHidden = false;
            }
            if (!menuHidden) { menuHidden = true; menuShadeWas = menuShade.alpha; }
            menuShade.alpha = 0f; menuShade.blocksRaycasts = false;
        }
        static void MenuShow()
        {
            if (!menuHidden) return;
            menuHidden = false;
            if (menuShade != null) { menuShade.alpha = menuShadeWas > 0.01f ? menuShadeWas : 1f; menuShade.blocksRaycasts = true; }
        }
        /* the pause menu behind the window takes no presses while it is up, and gets its focus back after */
        static CanvasGroup menuGroup; static GameObject menuWasSelected;
        static void MenuLock(bool on)
        {
            Menu m = Menu.instance;
            if (m == null || m.m_menuDialog == null) return;
            if (menuGroup == null || menuGroup.transform != m.m_menuDialog)
            {
                menuGroup = m.m_menuDialog.GetComponent<CanvasGroup>();
                if (menuGroup == null) menuGroup = m.m_menuDialog.gameObject.AddComponent<CanvasGroup>();
            }
            menuGroup.interactable = !on; menuGroup.blocksRaycasts = !on;
            if (on) MenuHide(); else MenuShow();
            EventSystem es = EventSystem.current;
            if (es == null) return;
            if (on) { menuWasSelected = es.currentSelectedGameObject; es.SetSelectedGameObject(null); }
            else if (menuWasSelected != null && menuWasSelected.activeInHierarchy) es.SetSelectedGameObject(menuWasSelected);
        }

        // ----- the button under the pause menu's controller picture -----
        // A full button copied from the game's own dialog buttons (the Quit dialog's), with Select's glyph beside its name.
        // Shown only while the pause menu shows the controller picture, in a game, so wherever the picture is seen the
        // button is under it. Select opens the window from there too (and does not open the map while it does).
        static GameObject menuBtn; static Menu btnFor; static bool btnLogged;
        static readonly FieldInfo F_settingsInst = AccessTools.Field(typeof(Menu), "m_settingsInstance");
        public static bool MenuButtonUp { get { return menuBtn != null && menuBtn.activeInHierarchy; } }
        static bool WantButton(Menu m)
        {
            return m != null && Menu.IsVisible() && Plugin.Enabled.Value && ZInput.IsGamepadActive() && !Open
                && InventoryGui.instance != null && Player.m_localPlayer != null && (F_settingsInst == null || F_settingsInst.GetValue(m) as GameObject == null)
                && m.m_gamepadRoot != null && m.m_gamepadRoot.activeInHierarchy;
        }
        /* the pause menu shown on keyboard and mouse (or with the mod off): the list and the picture go back to the game's places */
        static bool WantGameLayout(Menu m)
        {
            return m == null || (Menu.IsVisible() && !Open && (!Plugin.Enabled.Value || !ZInput.IsGamepadActive() || m.m_gamepadRoot == null || !m.m_gamepadRoot.activeInHierarchy));
        }
        // The layout is also set just before the screen is drawn (1.0.0, Rob saw the parts nudge into place on pausing): the
        // game shows its menu after the mod's own update, so for one frame it was drawn where the game has it.
        static bool layoutHooked;
        static void LayoutPass()
        {
            try
            {
                Menu m = Menu.instance;
                bool want = WantButton(m);
                if (want && (menuBtn == null || btnFor != m)) BuildMenuButton(m);
                if (menuBtn != null && menuBtn.activeSelf != want) menuBtn.SetActive(want);
                if (want && menuBtn != null) PlaceMenuButton(m);
                else if (WantGameLayout(m)) Lift(m, 0f);
            }
            catch (Exception e) { Canvas.willRenderCanvases -= LayoutPass; Plugin.Log.LogWarning("Pause layout: " + e); }
        }
        public static void MenuTick()
        {
            Menu m = Menu.instance;
            if (!layoutHooked) { layoutHooked = true; Canvas.willRenderCanvases += LayoutPass; }
            TextStyle.MeasureHeader(m != null && F_settingsInst != null ? F_settingsInst.GetValue(m) as GameObject : null);
            bool want = WantButton(m);
            if (want && (menuBtn == null || btnFor != m)) BuildMenuButton(m);
            if (menuBtn != null && menuBtn.activeSelf != want) menuBtn.SetActive(want);
            if (want && menuBtn != null)
            {
                PlaceMenuButton(m);
                EventSystem es = EventSystem.current;
                if (es != null)
                {
                    /* back from the window it opened: focus on the button again (it was hidden while the window was up) */
                    if (menuWasSelected == menuBtn) { menuWasSelected = null; if (es.currentSelectedGameObject == null) es.SetSelectedGameObject(menuBtn); }
                }
            }
            if (!want && WantGameLayout(m)) Lift(m, 0f);   /* kept between pauses, so the next pause opens already laid out */
            if (Open && (overMenu || overWorld)) { Drive(); return; }
            if (want && menuBtn != null && Core.RawDown("JoyMap")) { Show(InventoryGui.instance, true); return; }
            /* LT + Select in play (the game's chat combination), with the setting on */
            if (!Open && LtSelectOn && InventoryGui.instance != null && Player.m_localPlayer != null && !Menu.IsVisible() && !InventoryGui.IsVisible()
                && !Minimap.IsOpen() && (Chat.instance == null || !Chat.instance.HasFocus()) && Core.RawHeld("JoyLTrigger") && (Core.RawDown("JoyMap") || Core.RawDown("JoyChat")))   /* LT held and Select, read as buttons (1.0.0): the game's JoyChat is the alt key + Select, and the alt key is LB, not LT, in some layouts (the PRO one included) */
            {
                Show(InventoryGui.instance, false);
                overWorld = true;
            }
        }
        // ----- the button: a copy of the game's own dialog button (the Quit dialog's), with Select's glyph beside its name -----
        // 1.0.0 (Rob): back to this look after a short try of the mod's picture as the button. Under the controller picture,
        // reached with the D-pad down from the last entry of the menu list, lit with the game button's own focus look; A or
        // Select opens the window.
        static void BuildMenuButton(Menu m)
        {
            btnFor = m;
            if (menuBtn != null) UnityEngine.Object.Destroy(menuBtn);
            Button src = null;
            foreach (Transform d in new Transform[] { m.m_quitDialog, m.m_logoutDialog })
            {
                if (d == null || src != null) continue;
                Button[] all = d.GetComponentsInChildren<Button>(true);
                if (all.Length > 0) src = all[0];
            }
            if (src == null) src = m.m_continueButton;
            if (src == null) { if (!btnLogged) { btnLogged = true; Plugin.Log.LogWarning("Settings button: no game button to copy"); } return; }
            menuBtn = UnityEngine.Object.Instantiate(src.gameObject, m.m_gamepadRoot.transform, false);
            menuBtn.name = "ControllerPro_SettingsButton";
            string parts = "";
            foreach (Component c in menuBtn.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                parts += " " + n;
                /* the game's own button hint (its key and glyph) and its translation: the copy writes its own */
                if (n == "UIGamePad" || n == "UIInputHint" || n == "Localize") UnityEngine.Object.Destroy(c);
            }
            Button b = menuBtn.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(delegate { if (!Open && InventoryGui.instance != null) Show(InventoryGui.instance, true); });
            /* the D-pad: down goes to the first entry of the menu list (Continue); up from that entry comes here (by itself when
               the list finds its neighbours on its own, set on it when the list names them) */
            Selectable first = TopOfList(m);
            Navigation nav = new Navigation(); nav.mode = Navigation.Mode.Explicit; nav.selectOnDown = first; b.navigation = nav;
            if (first != null)
            {
                Navigation fn = first.navigation;
                if (fn.mode == Navigation.Mode.Explicit) { fn.selectOnUp = b; first.navigation = fn; }
                Plugin.Log.LogInfo("Settings button: over '" + first.name + "' with the D-pad (its navigation " + fn.mode + ")");
            }
            TMP_Text[] texts = menuBtn.GetComponentsInChildren<TMP_Text>(true);
            TMP_Text label = null;
            foreach (TMP_Text x in texts) { if (label == null && x.gameObject.activeSelf) label = x; else x.gameObject.SetActive(false); }
            if (label != null) { label.enableAutoSizing = false; label.enableWordWrapping = false; label.text = Hints.Glyph("JoyMap") + "  Controller Support Plus";   /* "Settings" dropped from the button (0.92.9) */ }
            RectTransform rt = (RectTransform)menuBtn.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            /* as wide as its name and glyph plus the game's own padding (0.87.5; a fixed 460 until then): the padding is what the
               game's button leaves around its own label, at least 24 a side */
            float srcW = rt.rect.width, srcLabel = 0f;
            if (label != null) { TMP_Text srcText = src.GetComponentInChildren<TMP_Text>(true); if (srcText != null) srcLabel = srcText.GetPreferredValues(srcText.text).x; }
            float pad = srcLabel > 1f ? Mathf.Clamp((srcW - srcLabel) * 0.5f, 24f, 60f) : 32f;
            float want = label != null ? label.GetPreferredValues(label.text).x + 2f * pad : 460f;
            rt.sizeDelta = new Vector2(Mathf.Max(want, 160f), Mathf.Max(rt.rect.height, 48f));
            Plugin.Log.LogInfo("Settings button: " + rt.sizeDelta.x + " wide (text " + (want - 2f * pad) + ", padding " + pad + " a side, the game's button " + srcW + " around a " + srcLabel + " label)");
            if (label != null) { RectTransform lt = label.rectTransform; lt.anchorMin = Vector2.zero; lt.anchorMax = Vector2.one; lt.offsetMin = new Vector2(pad, lt.offsetMin.y); lt.offsetMax = new Vector2(-pad, lt.offsetMax.y); label.alignment = TextAlignmentOptions.Center; }
            menuBtn.SetActive(false);
            Plugin.Log.LogInfo("Settings button: copied the game's '" + src.name + "' (" + rt.sizeDelta + "):" + parts);
        }
        /* the first entry of the pause menu's list (Continue) */
        static Selectable TopOfList(Menu m)
        {
            if (m.m_continueButton == null) return null;
            Selectable best = m.m_continueButton; float high = float.MinValue;
            foreach (Button lb in m.m_continueButton.transform.parent.GetComponentsInChildren<Button>(true))
            {
                if (lb == null || !lb.gameObject.activeSelf) continue;
                ((RectTransform)lb.transform).GetWorldCorners(lc);
                if (lc[1].y > high) { high = lc[1].y; best = lb; }
            }
            return best;
        }

        // ----- the pause screen's layout with the controller picture (1.0.0, Rob) -----
        // One column centred on the screen: the button, the menu list (with the little header art over it), then the controller
        // picture, a set gap apart, measured by what is drawn (the texts' own letters, not their boxes), so Quit no longer
        // runs into the picture. Only the list, its header and the picture are moved, as wholes; they go back to the game's own
        // places whenever the picture is not shown (keyboard and mouse).
        const float BottomMargin = 24f; const float ColumnGap = 14f,   /* 28 until Rob's second look: still too far apart */ HeaderReach = 260f;
        static readonly Vector3[] lc = new Vector3[4];
        static Transform lyList, lyArt; static Vector3 lyListOrig, lyArtOrig, lyListAt, lyArtAt, lyBtnAt; static int lyW, lyH; static bool lyLogged;
        static readonly List<Transform> lyWith = new List<Transform>(); static readonly List<Vector3> lyWithOrig = new List<Vector3>(), lyWithAt = new List<Vector3>();
        static void Lift(Menu m, float unused)
        {
            if (lyList == null && lyArt == null) return;
            if (lyList != null) lyList.localPosition = lyListOrig;
            if (lyArt != null) lyArt.localPosition = lyArtOrig;
            for (int i = 0; i < lyWith.Count; i++) if (lyWith[i] != null) lyWith[i].localPosition = lyWithOrig[i];
            lyWith.Clear(); lyWithOrig.Clear(); lyWithAt.Clear();
            lyList = null; lyArt = null;
        }
        static readonly Vector4 NoRect = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        /* what a graphic really draws, in the world: a text's letters, anything else its rectangle */
        static void Grow(ref Vector4 r, Graphic g)
        {
            TMP_Text t = g as TMP_Text;
            if (t != null)
            {
                if (string.IsNullOrEmpty(t.text)) return;
                t.ForceMeshUpdate();   /* its letters measured even on the first frame it shows */
                Bounds b = t.textBounds;
                if (b.size.x < 0.01f || b.size.y < 0.01f) return;
                Vector3 p0 = t.transform.TransformPoint(b.min), p1 = t.transform.TransformPoint(b.max);
                r.x = Mathf.Min(r.x, Mathf.Min(p0.x, p1.x)); r.y = Mathf.Min(r.y, Mathf.Min(p0.y, p1.y)); r.z = Mathf.Max(r.z, Mathf.Max(p0.x, p1.x)); r.w = Mathf.Max(r.w, Mathf.Max(p0.y, p1.y));
                return;
            }
            g.rectTransform.GetWorldCorners(lc);
            r.x = Mathf.Min(r.x, lc[0].x); r.y = Mathf.Min(r.y, lc[0].y); r.z = Mathf.Max(r.z, lc[2].x); r.w = Mathf.Max(r.w, lc[2].y);
        }
        static bool Drawn(Graphic g)
        {
            if (g == null || !g.enabled || g.color.a < 0.01f) return false;
            if (menuBtn != null && g.transform.IsChildOf(menuBtn.transform)) return false;
            g.rectTransform.GetWorldCorners(lc);
            return lc[2].x - lc[0].x < Screen.width * 0.9f && lc[2].y - lc[0].y < Screen.height * 0.9f;   /* no full-screen ground */
        }
        /* the world rectangle of everything drawn under a part (x0, y0, x1, y1), leaving out one part */
        static Vector4 Bounds(Transform t, Transform skip)
        {
            Vector4 r = NoRect;
            foreach (Graphic g in t.GetComponentsInChildren<Graphic>(false))
                if (Drawn(g) && (skip == null || !g.transform.IsChildOf(skip))) Grow(ref r, g);
            return r;
        }
        /* the header art over the menu list: what the menu draws just above the list, within its width, outside the list and
           the picture; moved with the list */
        static void FindHeader(Menu m, Transform list, Transform art, Vector4 l, float k)
        {
            Transform menuRoot = m.m_root != null ? m.m_root : (m.m_menuDialog != null ? m.m_menuDialog : list.parent);
            string names = "";
            foreach (Graphic g in menuRoot.GetComponentsInChildren<Graphic>(false))
            {
                if (!Drawn(g)) continue;
                Transform gt = g.transform;
                if (gt.IsChildOf(list) || gt.IsChildOf(art) || list.IsChildOf(gt) || art.IsChildOf(gt)) continue;
                Vector4 r = NoRect; Grow(ref r, g);
                if (r.x > r.z) continue;
                float mid = (r.x + r.z) * 0.5f;
                if (mid < l.x || mid > l.z || r.y < l.w - 4f * k || r.y > l.w + HeaderReach * k) continue;
                bool under = false;
                foreach (Transform w in lyWith) if (gt.IsChildOf(w)) { under = true; break; }
                if (under) continue;
                lyWith.RemoveAll(delegate(Transform w) { return w.IsChildOf(gt); });
                lyWith.Add(gt);
                names += " '" + gt.name + "'";
            }
            foreach (Transform w in lyWith) lyWithOrig.Add(w.localPosition);
            if (!lyLogged) Plugin.Log.LogInfo("Pause layout: header over the list:" + (names.Length > 0 ? names : " none found"));
        }
        static Transform Mover(Transform t, Transform avoid)
        {
            while (t.parent != null && t.parent.GetComponent<LayoutGroup>() != null && !avoid.IsChildOf(t.parent) && !t.parent.IsChildOf(avoid)) t = t.parent;
            return t;
        }
        static void PlaceMenuButton(Menu m)
        {
            RectTransform rt = (RectTransform)menuBtn.transform;
            Transform list = m.m_continueButton != null ? m.m_continueButton.transform.parent : null;
            Transform art = m.m_gamepadRoot != null ? m.m_gamepadRoot.transform : null;
            if (list == null || art == null || list == art || art.IsChildOf(list)) return;
            /* the game lays its entries out itself (a layout group, rebuilt as the menu shows): the part moved is the first one up
               that no layout places, or the game would put it back between the mod's move and the screen being drawn */
            list = Mover(list, art); art = Mover(art, list);
            if (list != lyList || art != lyArt || Screen.width != lyW || Screen.height != lyH)
            {
                Lift(m, 0f);
                /* the entries where the game will have them once its layout is done, before they are measured */
                for (Transform t = m.m_continueButton.transform.parent; t != null; t = t.parent)
                {
                    RectTransform r = t as RectTransform;
                    if (r != null && t.GetComponent<LayoutGroup>() != null) LayoutRebuilder.ForceRebuildLayoutImmediate(r);
                    if (t == list) break;
                }
                lyList = list; lyArt = art; lyListOrig = list.localPosition; lyArtOrig = art.localPosition; lyW = Screen.width; lyH = Screen.height;
                /* measured where the game has them */
                Vector4 a = Bounds(art, list), l = Bounds(list, null);
                if (l.x > l.z || a.x > a.z) { lyListAt = list.position; lyArtAt = art.position; lyBtnAt = rt.position; Plugin.Log.LogWarning("Pause layout: the list or the picture was not found, left as the game has them"); return; }
                float k = rt.lossyScale.y, bh = rt.rect.height * k, gap = ColumnGap * k;
                FindHeader(m, list, art, l, k);
                Vector4 top = l;   /* the list with its header */
                foreach (Transform w in lyWith) foreach (Graphic g in w.GetComponentsInChildren<Graphic>(false)) if (Drawn(g)) Grow(ref top, g);
                float hh = top.w - top.y, ah = a.w - a.y;
                float total = hh + gap + ah + gap + bh;
                float cx = Screen.width * 0.5f;
                float bTop = Mathf.Min(Screen.height - gap, BottomMargin * rt.lossyScale.y + total),   /* low on the screen, the picture just above its bottom edge (Rob's fourth look; centred before) */ hTop = bTop - bh - gap, aTop = hTop - hh - gap;   /* the button on top since Rob's third look; under the picture before */
                /* centred on the entries' own words (Continue, Save...), not on the art around them, which reaches further left */
                Vector4 words = NoRect;
                foreach (TMP_Text t in list.GetComponentsInChildren<TMP_Text>(false)) if (Drawn(t)) Grow(ref words, t);
                float listMid = words.x <= words.z ? (words.x + words.z) * 0.5f : (l.x + l.z) * 0.5f;
                Vector3 d = new Vector3(cx - listMid, hTop - top.w, 0f);
                lyListAt = list.position + d;
                foreach (Transform w in lyWith) lyWithAt.Add(w.position + d);
                /* centred on the controller drawing itself, not on its labels (they reach further out on the left) */
                float artMid = (a.x + a.z) * 0.5f, biggest = 0f;
                foreach (Graphic g in art.GetComponentsInChildren<Graphic>(false))
                {
                    if (!(g is Image || g is RawImage) || !Drawn(g) || g.transform.IsChildOf(list)) continue;
                    g.rectTransform.GetWorldCorners(lc);
                    float area = (lc[2].x - lc[0].x) * (lc[2].y - lc[0].y);
                    if (area > biggest) { biggest = area; artMid = (lc[0].x + lc[2].x) * 0.5f; }
                }
                lyArtAt = art.position + new Vector3(cx - artMid, aTop - a.w, 0f);
                lyBtnAt = new Vector3(cx, bTop - bh * 0.5f, rt.position.z);
                if (!lyLogged)
                {
                    lyLogged = true;
                    Plugin.Log.LogInfo("Pause layout: moving '" + list.name + "' (the list) and '" + art.name + "' (the picture)");
                    Plugin.Log.LogInfo("Pause layout: list and header " + hh + " high (its words' middle " + listMid + ", all of it " + (l.x + l.z) * 0.5f + "), picture " + ah + " high (" + (a.z - a.x) + " wide), button " + bh + ", gap " + gap + ", column " + total + " on a " + Screen.width + " x " + Screen.height + " screen");
                }
            }
            /* kept in place every frame: the button last, as it sits in the picture's part it moves with */
            if ((list.position - lyListAt).sqrMagnitude > 0.25f) list.position = lyListAt;
            for (int i = 0; i < lyWith.Count && i < lyWithAt.Count; i++) if (lyWith[i] != null && (lyWith[i].position - lyWithAt[i]).sqrMagnitude > 0.25f) lyWith[i].position = lyWithAt[i];
            if ((art.position - lyArtAt).sqrMagnitude > 0.25f) art.position = lyArtAt;
            if ((rt.position - lyBtnAt).sqrMagnitude > 0.25f) rt.position = lyBtnAt;
        }
        static void Show(InventoryGui gui, bool menu)
        {
            if (root == null || root.transform.parent == null) Build(gui);
            if (root == null) return;
            overMenu = menu;
            OntoHud();
            if (menu) MenuLock(true);
            Open = true; openedFrame = Time.frameCount;
            tabNow = 0; focus = FirstRow(0);   /* opens on General */
            root.SetActive(true);
            root.transform.SetAsLastSibling();
            panel.position = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, panel.position.z);
            Refresh();
            shownAt = Time.unscaledTime;
        }

        // once, a second after the window first shows: what the rows are drawn with, to the log
        static float shownAt = -1f; static bool reported;
        static void Report()
        {
            if (reported || shownAt < 0f || Time.unscaledTime - shownAt < 1f) return;
            reported = true;
            string s = "";
            foreach (Row r in rows)
            {
                RectTransform rt = r.value != null ? r.value.rectTransform : null;
                if (rt == null) continue;
                Vector3[] q = new Vector3[4]; rt.GetWorldCorners(q);
                s += " | " + r.name + " at " + q[0] + "-" + q[2];
            }
            Plugin.Log.LogInfo("Settings window rows:" + s + " | panel at " + panel.position + " size " + panel.rect.size);
        }

        static void Build(InventoryGui gui)
        {
            TMP_Text like = gui.m_weight != null ? gui.m_weight : gui.m_containerName;
            Vector2 tl = new Vector2(0f, 1f), tr = new Vector2(1f, 1f), top = new Vector2(0.5f, 1f), bottom = new Vector2(0.5f, 0f);
            TMP_Text title;
            MeasureTabs(gui);
            float h = ContentTop + ContentH + FrameX;   /* the tabs' strip, the content box, then the same air under it as at its sides */
            root = Favorites.Shell(gui, "ControllerPro_Settings", new Vector2(WinW, h),   /* wide enough for the longest name beside the controls, wider for the tabs (0.92.25) */ "Controller Support Plus Settings", out panel, out title);
            root.SetActive(true);   // built switched on, as the Controller Hotbar window is: its texts wake up with the game's font
            BuildFrames(gui, like);
            for (int i = 0; i < rows.Length; i++)
            {
                Row r = rows[i];
                if (r.header) continue;   /* the groups are the tabs now (0.92.21): no name or line in the list */
                into = tabRoots[r.tab];
                float y = RowY(i);
                // the focused row's soft light, behind its name and value
                r.plate = Hints.Img(Hints.New("rowLight" + i, into), Hints.Plate(), new Color(1f, 1f, 1f, 0.08f));
                Hints.Place(r.plate.gameObject, top, top, top, new Vector2(0f, y), new Vector2(BoxW - 12f, RowH));
                r.label = Hints.Text(Hints.New("rowName" + i, into), like, 20f, Color.white, TextAlignmentOptions.MidlineLeft);
                Hints.Place(r.label.gameObject, tl, tl, tl, new Vector2(44f, y), new Vector2(r.check && r.sideText == null ? 540f : 300f, RowH));   /* a checkbox row's name has the room up to the box (or up to its side value) */
                r.label.enableWordWrapping = false;
                r.label.text = r.name;
                Look(r.label, like);
                TextStyle.Apply(r.label, TextStyle.Kind.Header);
                if (r.check && CheckBox(r, i, y)) { if (r.sideText != null) SideValue(r, i, y, like); continue; }
                if (r.act != null && RowButton(r, i, y)) continue;
                // the game's own two-arrow control (the Graphics preset's: two framed arrow buttons, a dark bar between)
                if (Stepper(r, i, y)) continue;
                // else the value between two arrows, in the game's text
                r.value = Hints.Text(Hints.New("rowValue" + i, into), like, 20f, Color.white, TextAlignmentOptions.Center);
                Hints.Place(r.value.gameObject, tr, tr, tr, new Vector2(-44f, y), new Vector2(240f, RowH));
                Look(r.value, like);
            }
            root.SetActive(false);
        }

        // ----- the tabs (0.92.21, Rob) -----
        static RectTransform into;              // where a row being built goes: its tab's own layer
        static RectTransform[] tabRoots;        // one layer per tab, the size of the window, only the shown tab's switched on
        static Button[] tabBtns;
#if DEV
        static GameObject resetBtn;   /* the corner's Reset mod (dev) */
#endif
        static readonly Color FrameGround = new Color(0f, 0f, 0f, 0.2f);   /* the onboarding cards' light black */
        static RectTransform FramedBox(string name, float topY, float height)
        {
            Vector2 top = new Vector2(0.5f, 1f);
            GameObject g = Hints.New(name, panel);
            RectTransform rt = Hints.Place(g, top, top, top, new Vector2(0f, -topY), new Vector2(BoxW, height));
            Image bg = Hints.Img(Hints.New("bg", g.transform), null, FrameGround); bg.raycastTarget = false;
            Hints.Place(bg.gameObject, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            HudPick.FrameOn(g.transform);   /* the cards' yellow frame */
            return rt;
        }
        static void BuildFrames(InventoryGui gui, TMP_Text like)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f), left = new Vector2(0f, 0.5f), right = new Vector2(1f, 0.5f);
            Vector2 bl0 = new Vector2(0f, 0f), top0 = new Vector2(0.5f, 1f);
            FramedBox("contentFrame", ContentTop, ContentH);
            // a layer per tab, the window's own size, so the rows keep their places measured from the window
            tabRoots = new RectTransform[tabNames.Length];
            for (int t = 0; t < tabNames.Length; t++)
                tabRoots[t] = Hints.Place(Hints.New("tab" + t, panel), Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
            // the tab row, no box of its own (0.92.24, Rob): LB, the tab buttons, RB, from the content box's left edge, their
            // bottoms on its top edge
            GameObject row = Hints.New("tabButtons", panel);
            RectTransform rowRt = Hints.Place(row, top0, top0, top0, new Vector2(0f, -TabsTop), new Vector2(BoxW, TabsH));
            rowRt.pivot = bl0; rowRt.anchoredPosition = new Vector2(-BoxW * 0.5f, -ContentTop);   /* held by its bottom left, on the box's top left corner */
            TMP_Text lb = Hints.Text(Hints.New("lb", row.transform), like, TextStyle.HintSize, Color.white, TextAlignmentOptions.Center);
            Hints.Place(lb.gameObject, bl0, bl0, bl0, Vector2.zero, new Vector2(GlyphW, TabsH)); lb.text = Hints.Glyph("JoyLBumper"); TextStyle.Apply(lb, TextStyle.Kind.Hint);
            // the crafting panel's own tab button, one per tab, side by side, each at least that button's own size
            Button src = gui.m_tabCraft != null ? gui.m_tabCraft : gui.m_tabUpgrade;
            float srcH = src != null ? ((RectTransform)src.transform).rect.height : TabsH;
            tabBtns = new Button[tabNames.Length];
            float x = GlyphW + TabGap;
            List<RectTransform> made = new List<RectTransform>();
            for (int t = 0; t < tabNames.Length; t++)
            {
                GameObject b;
                TMP_Text label = null;
                if (src != null)
                {
                    b = UnityEngine.Object.Instantiate(src.gameObject, row.transform, false);
                    b.SetActive(true);
                    foreach (Component c in b.GetComponentsInChildren<Component>(true))
                    {
                        if (c == null) continue;
                        string n = c.GetType().Name;
                        if (n == "UIGamePad" || n == "UIInputHint" || n == "Localize") UnityEngine.Object.Destroy(c);
                        /* the game's ButtonTextColor kept the name in the colour it had when copied (the open Craft tab's yellow) whatever the state: gone at once, the window colours the names itself (0.92.27) */
                        else if (n == "ButtonTextColor") UnityEngine.Object.DestroyImmediate(c);
                    }
                    foreach (TMP_Text tx in b.GetComponentsInChildren<TMP_Text>(true)) { if (label == null) label = tx; else tx.gameObject.SetActive(false); }
                    Button bt = b.GetComponent<Button>();
                    bt.onClick = new Button.ButtonClickedEvent();
                    int tt = t; bt.onClick.AddListener(delegate { GoTab(tt); });
                    Navigation nav = bt.navigation; nav.mode = Navigation.Mode.None; bt.navigation = nav;
                    tabBtns[t] = bt;
                }
                else
                {
                    b = Hints.New("tabButton" + t, row.transform);
                    label = Hints.Text(Hints.New("label", b.transform), like, 18f, Color.white, TextAlignmentOptions.Center);
                }
                b.name = "cp_tab" + t;
                if (label != null)
                {
                    label.enableAutoSizing = false; label.enableWordWrapping = false; label.alignment = TextAlignmentOptions.Center; label.text = tabNames[t].ToUpperInvariant();   /* in capitals, as the crafting tabs (0.92.24) */
                    if (src != null && tabFont > 1f) label.fontSize = tabFont;   /* the crafting tab's own text size (0.92.25) */
                    RectTransform lr = label.rectTransform; lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one; lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
                }
                // the button at the crafting panel's own size and units, scaled to look on screen as it does there
                float w = tabW != null && tabW[t] > 1f ? tabW[t] : (label != null ? label.GetPreferredValues(label.text).x : 100f) + 44f;
                float k = src != null ? tabK : 1f;
                RectTransform brt = (RectTransform)b.transform;
                brt.anchorMin = brt.anchorMax = brt.pivot = bl0;
                brt.sizeDelta = new Vector2(w, src != null ? srcH : TabsH);
                brt.anchoredPosition = new Vector2(x, 0f);
                brt.localScale = new Vector3(k, k, 1f);
                made.Add(brt);
                x += w * k + TabGap;
            }
            TMP_Text rb = Hints.Text(Hints.New("rb", row.transform), like, TextStyle.HintSize, Color.white, TextAlignmentOptions.Center);
            Hints.Place(rb.gameObject, bl0, bl0, bl0, new Vector2(x, 0f), new Vector2(GlyphW, TabsH)); rb.text = Hints.Glyph("JoyRBumper"); TextStyle.Apply(rb, TextStyle.Kind.Hint);
            float total = x + GlyphW, room = BoxW;
            if (total > room + 0.5f) rowRt.localScale = new Vector3(room / total, room / total, 1f);   /* only if the window could not grow enough: never past the box */
            Plugin.Log.LogInfo("Settings window: " + tabNames.Length + " tabs from " + (src != null ? "the crafting tab '" + src.name + "'" : "plain texts") + ", " + total + " wide on a " + room + " box");
#if DEV
            // Reset mod (dev): out of the window, in the screen's top left corner, a game button with Select's glyph; Select
            // presses it (0.92.21, Rob)
            Button bs = null;
            Menu m0 = Menu.instance;
            if (m0 != null) foreach (Transform d in new Transform[] { m0.m_quitDialog, m0.m_logoutDialog })
                { if (d == null || bs != null) continue; Button[] all = d.GetComponentsInChildren<Button>(true); if (all.Length > 0) bs = all[0]; }
            if (bs != null)
            {
                resetBtn = UnityEngine.Object.Instantiate(bs.gameObject, root.transform, false);
                resetBtn.name = "cp_resetDev"; resetBtn.SetActive(true);
                foreach (Component c in resetBtn.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    string n = c.GetType().Name;
                    if (n == "UIGamePad" || n == "UIInputHint" || n == "Localize") UnityEngine.Object.Destroy(c);
                }
                Button rbtn = resetBtn.GetComponent<Button>();
                rbtn.onClick = new Button.ButtonClickedEvent(); rbtn.onClick.AddListener(delegate { HudPick.DevReset(); });
                Navigation rn = rbtn.navigation; rn.mode = Navigation.Mode.None; rbtn.navigation = rn;
                TMP_Text rl = null;
                foreach (TMP_Text tx in resetBtn.GetComponentsInChildren<TMP_Text>(true)) { if (rl == null && tx.gameObject.activeSelf) rl = tx; else tx.gameObject.SetActive(false); }
                string txt = Hints.Glyph("JoyMap") + "  Reset mod (dev)";
                float rw = 260f;
                if (rl != null) { rl.enableAutoSizing = false; rl.enableWordWrapping = false; rl.text = txt; rw = rl.GetPreferredValues(txt).x + 64f; }
                RectTransform rrt = (RectTransform)resetBtn.transform;
                Vector2 tl0 = new Vector2(0f, 1f);
                rrt.anchorMin = rrt.anchorMax = rrt.pivot = tl0;
                rrt.sizeDelta = new Vector2(Mathf.Max(rw, 160f), Mathf.Max(rrt.rect.height, 44f));
                rrt.anchoredPosition = new Vector2(30f, -30f);
            }
#endif
        }
        /* the shown tab's layer on, its button in the crafting panel's chosen look (the game shows the open tab's button
           switched off), the others pressable */
        static void ShowTab()
        {
            if (tabRoots == null) return;
            for (int t = 0; t < tabRoots.Length; t++)
            {
                if (tabRoots[t] != null && tabRoots[t].gameObject.activeSelf != (t == tabNow)) tabRoots[t].gameObject.SetActive(t == tabNow);
                if (tabBtns != null && tabBtns[t] != null && tabBtns[t].interactable != (t != tabNow)) tabBtns[t].interactable = t != tabNow;
                /* the name's colour for its state, as the crafting panel's two tabs show theirs (0.92.26, Rob: the closed tabs
                   kept the open one's colour) */
                TMP_Text l = tabBtns != null && tabBtns[t] != null ? TabLabelOf(tabBtns[t]) : null;
                if (l != null) { Color want = t == tabNow ? TabOnColour() : TabOffColour(); if (l.color != want) l.color = want; }
            }
        }
        // The crafting panel's own tab name colours: the open tab's (its button switched off) and a closed one's, read from
        // its Craft and Upgrade tabs whenever they differ; until then the colours seen in the game (Rob's picture, 0.92.26).
        static Color tabOn = new Color(1f, 0.718f, 0.36f, 1f), tabOff = new Color(0.846f, 0.846f, 0.846f, 1f);   /* the game's own, as read in 0.92.26 */
        static bool tabColoursRead, tabLooksLogged;
        static void ReadTabColours()
        {
            if (tabColoursRead) return;
            InventoryGui gui = InventoryGui.instance;
            Button c = gui != null ? gui.m_tabCraft : null, u = gui != null ? gui.m_tabUpgrade : null;
            TMP_Text cl = TabLabelOf(c), ul = TabLabelOf(u);
            if (!tabLooksLogged && c != null)
            {
                tabLooksLogged = true;
                string parts = ""; foreach (Component x in c.GetComponentsInChildren<Component>(true)) if (x != null) parts += " " + x.GetType().Name;
                Plugin.Log.LogInfo("Settings window: crafting tabs: Craft " + (c.interactable ? "closed" : "open") + " text " + (cl != null ? cl.color.ToString() : "-") + ", Upgrade " + (u != null && u.interactable ? "closed" : "open") + " text " + (ul != null ? ul.color.ToString() : "-") + ", transition " + c.transition + " on " + (c.targetGraphic != null ? c.targetGraphic.name : "-") + " | parts:" + parts);
            }
            if (cl == null || ul == null || c.interactable == u.interactable || cl.color == ul.color) return;
            tabOn = c.interactable ? ul.color : cl.color; tabOff = c.interactable ? cl.color : ul.color;
            tabColoursRead = true;
            Plugin.Log.LogInfo("Settings window: tab name colours from the crafting panel: open " + tabOn + ", closed " + tabOff);
        }
        static Color TabOnColour() { ReadTabColours(); return tabOn; }
        static Color TabOffColour() { ReadTabColours(); return tabOff; }



        // The Settings screen's own label look (the yellow names beside its controls: Resolution, VSync...): copied from the
        // label beside its Resolution dropdown, or the first yellow label it has. White if none is found.
        static Color labelColour = Color.white;
        /* the Settings screen's grey text (the most common grey it draws), for the groups' names */
        static Color greyColour; static bool greyLooked;
        static Color GreyLook()
        {
            if (greyLooked) return greyColour;
            greyLooked = true; greyColour = new Color(0.62f, 0.62f, 0.62f, 1f);
            Menu m = Menu.instance;
            GameObject prefab = m != null ? m.m_settingsPrefab : null;
            if (prefab == null) return greyColour;
            Dictionary<Color32, int> seen = new Dictionary<Color32, int>();
            foreach (TMP_Text l in prefab.GetComponentsInChildren<TMP_Text>(true))
            {
                Color c = l.color;
                float hi = Mathf.Max(c.r, Mathf.Max(c.g, c.b)), lo = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                if (hi - lo > 0.06f || hi < 0.35f || hi > 0.85f || c.a < 0.5f) continue;
                Color32 k = c; int n; seen.TryGetValue(k, out n); seen[k] = n + 1;
            }
            int most = 0;
            foreach (KeyValuePair<Color32, int> kv in seen) if (kv.Value > most) { most = kv.Value; greyColour = kv.Key; }
            Plugin.Log.LogInfo("Settings window: group names in the Settings screen's grey " + greyColour + " (" + most + " texts)");
            return greyColour;
        }
        const float LabelSize = 20f;   // 22 read a touch large (0.71.1-0.71.2)
        static TMP_Text labelLike; static bool labelLooked;
        static void LabelLook(TMP_Text x)
        {
            if (!labelLooked)
            {
                labelLooked = true;
                Menu m = Menu.instance;
                GameObject prefab = m != null ? m.m_settingsPrefab : null;
                if (prefab != null)
                {
                    // The row labels (Resolution, VSync...) share one size; the window's title and tab names are larger and
                    // fewer. Take the Resolution label itself, else the size most yellow labels share. Every yellow label
                    // is written to the log once, to check. (0.70.3 took the title, "Settings" at 32.)
                    List<TMP_Text> yellow = new List<TMP_Text>();
                    Dictionary<float, int> sizes = new Dictionary<float, int>();
                    string seen = "";
                    foreach (TMP_Text l in prefab.GetComponentsInChildren<TMP_Text>(true))
                    {
                        if (l.GetComponentInParent<TMP_Dropdown>() != null) continue;
                        Color c = l.color;
                        if (!(c.r > 0.8f && c.g > 0.45f && c.g < 0.85f && c.b < 0.35f)) continue;
                        yellow.Add(l);
                        int k; sizes.TryGetValue(l.fontSize, out k); sizes[l.fontSize] = k + 1;
                        seen += " '" + l.text + "' " + l.fontSize + (l.enableAutoSizing ? "(auto " + l.fontSizeMin + "-" + l.fontSizeMax + ")" : "") + " " + l.font.name + " " + l.fontStyle + ";";
                        if (l.text != null && l.text.ToLowerInvariant().Contains("resolution") && !l.text.ToLowerInvariant().Contains("3d")) labelLike = l;
                    }
                    if (labelLike == null)
                    {
                        float common = 0f; int most = 0;
                        foreach (KeyValuePair<float, int> kv in sizes) if (kv.Value > most) { most = kv.Value; common = kv.Key; }
                        foreach (TMP_Text l in yellow) if (l.fontSize == common) { labelLike = l; break; }
                    }
                    Plugin.Log.LogInfo("Settings window: the Settings screen's yellow labels:" + seen);
                }                if (labelLike != null) labelColour = labelLike.color;
                Plugin.Log.LogInfo("Settings window: label look " + (labelLike != null ? "'" + labelLike.text + "' " + labelLike.font.name + " " + labelLike.fontSize + " " + labelLike.color + " " + labelLike.fontStyle : "not found"));
            }
            if (labelLike == null || x == null) return;
            x.font = labelLike.font; x.fontSharedMaterial = labelLike.fontSharedMaterial; x.fontStyle = labelLike.fontStyle;
            x.color = labelColour; x.characterSpacing = labelLike.characterSpacing;
            // Its size is not copied: the Settings screen draws its labels at 14 on a larger-scaled screen of its own, and
            // 14 here came out far smaller than the window's white text (0.70.4-0.71.0). A little larger than the values.
            x.enableAutoSizing = false; x.fontSize = LabelSize;
        }

        // The Graphics tab's preset control, copied for a row: its arrow buttons cut loose and given this row's steps, its own
        // name hidden (the row has its own), its value text kept for this row's value. Placed so its right arrow ends where
        // the dropdown does. False when the game's control cannot be found.
        // The Graphics tab's VSync checkbox (or the first checkbox the Settings screen has), copied for a row: its own name
        // hidden, its tick showing the row's value, cut loose and not clickable (A ticks it through the row). Placed so its
        // right edge is where the arrows' right edge is. False when none can be found.
        static readonly FieldInfo F_vsync = AccessTools.Field(typeof(Valheim.SettingsGui.GraphicsSettings), "m_vsyncToggle");
        static bool checkLogged;
        const float CheckSize = 34f;   // the box's height in the window: about the arrows' framed buttons
        // the screen box of what is drawn under t (enabled graphics)
        static Rect Drawn(Transform t)
        {
            Vector3[] c = new Vector3[4];
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (Graphic g in t.GetComponentsInChildren<Graphic>(false))
            {
                if (!g.enabled || g is TMP_Text) continue;
                g.rectTransform.GetWorldCorners(c);
                for (int i = 0; i < 4; i++) { x0 = Mathf.Min(x0, c[i].x); y0 = Mathf.Min(y0, c[i].y); x1 = Mathf.Max(x1, c[i].x); y1 = Mathf.Max(y1, c[i].y); }
            }
            return x0 == float.MaxValue ? new Rect() : Rect.MinMaxRect(x0, y0, x1, y1);
        }
        static bool CheckBox(Row r, int i, float y)
        {
            Menu m = Menu.instance;
            GameObject prefab = m != null ? m.m_settingsPrefab : null;
            Valheim.SettingsGui.GraphicsSettings gs = prefab != null ? prefab.GetComponentInChildren<Valheim.SettingsGui.GraphicsSettings>(true) : null;
            Toggle src = gs != null && F_vsync != null ? F_vsync.GetValue(gs) as Toggle : null;
            if (src == null && prefab != null) { Toggle[] all = prefab.GetComponentsInChildren<Toggle>(true); if (all.Length > 0) src = all[0]; }
            if (src == null) { if (!checkLogged) { checkLogged = true; Plugin.Log.LogWarning("Settings window: the game's checkbox was not found"); } return false; }
            GameObject copy = UnityEngine.Object.Instantiate(src.gameObject, into, false);
            copy.name = "cp_check" + i; copy.SetActive(true);
            Toggle tg = copy.GetComponent<Toggle>();
            tg.onValueChanged = new Toggle.ToggleEvent(); tg.group = null;
            r.lits.Add(Lit.Of(tg)); tg.interactable = false;
            Navigation nav = tg.navigation; nav.mode = Navigation.Mode.None; tg.navigation = nav;
            string parts = "";
            foreach (TMP_Text x in copy.GetComponentsInChildren<TMP_Text>(true)) { parts += " '" + x.text + "'"; x.gameObject.SetActive(false); }   // its own name: the row has its own
            r.box = tg;
            // Placed by its own frame (the game's box is its whole object, 20 by 20): its right edge 44 in from the window's
            // right edge, as the arrows' right edge is, centred on the row, and grown from that edge to about the arrows'
            // framed buttons' size (the Settings screen draws it on its own larger scale). Until 0.78.6 it was placed by
            // everything it draws, part of which reaches further than the box, and it sat to the left of the arrows' edge.
            RectTransform crt = (RectTransform)copy.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(1f, 0.5f);
            crt.anchoredPosition = new Vector2(-44f, y - RowH * 0.5f);
            float h = crt.rect.height > 1f ? crt.rect.height : 20f;
            float grow = CheckSize / h;
            crt.localScale = new Vector3(grow, grow, 1f);            if (!checkLogged)
            {
                checkLogged = true;
                Plugin.Log.LogInfo("Settings window: copied the game's checkbox '" + src.name + "' (" + crt.rect.size + "), its texts hidden:" + parts);
            }
            return true;
        }

        static readonly FieldInfo F_gpRoot = AccessTools.Field(typeof(Valheim.SettingsGui.GraphicsSettings), "m_graphicPresetsRoot");
        static readonly FieldInfo F_gpLeft = AccessTools.Field(typeof(Valheim.SettingsGui.GraphicsSettings), "m_graphicPresetLeft");
        static readonly FieldInfo F_gpRight = AccessTools.Field(typeof(Valheim.SettingsGui.GraphicsSettings), "m_graphicPresetRight");
        static bool stepLogged;
        static string PathIn(Transform t, Transform root) { string p = ""; while (t != null && t != root) { p = t.name + (p == "" ? "" : "/" + p); t = t.parent; } return p; }
        // The value beside a checkbox (1.0.2, Rob): a copy of an arrows row's value text (the game's preset arrows' own
        // 'Value' text), so it reads as the arrows' value does; right-aligned, ending a gap to the left of the box.
        static void SideValue(Row r, int i, float y, TMP_Text like)
        {
            TMP_Text tpl = null;
            foreach (Row o in rows) if (o.stepper && o.value != null) { tpl = o.value; break; }
            TMP_Text t;
            if (tpl != null)
            {
                t = UnityEngine.Object.Instantiate(tpl.gameObject, into, false).GetComponent<TMP_Text>();
                foreach (Component c in t.GetComponentsInChildren<Component>(true)) if (c != null && c.GetType().Name == "Localize") UnityEngine.Object.Destroy(c);
            }
            else { t = Hints.Text(Hints.New("rowSide" + i, into), like, 20f, Color.white, TextAlignmentOptions.MidlineRight); Look(t, like); }
            t.name = "rowSide" + i;
            t.gameObject.SetActive(true);
            t.enableWordWrapping = false; t.enableAutoSizing = false;
            t.alignment = TextAlignmentOptions.MidlineRight;
            /* the copy leaves the arrows' own scale behind: its size is matched so it reads the same on screen */
            if (tpl != null) { t.transform.localScale = Vector3.one; float a = tpl.transform.lossyScale.y, b = into.lossyScale.y; if (a > 0f && b > 0f) t.fontSize = tpl.fontSize * a / b; }
            Vector2 tr = new Vector2(1f, 1f);
            Hints.Place(t.gameObject, tr, tr, tr, new Vector2(-(44f + CheckSize + 14f), y), new Vector2(240f, RowH));
            r.side = t;
        }

        static bool Stepper(Row r, int i, float y)
        {
            Menu m = Menu.instance;
            GameObject prefab = m != null ? m.m_settingsPrefab : null;
            Valheim.SettingsGui.GraphicsSettings gs = prefab != null ? prefab.GetComponentInChildren<Valheim.SettingsGui.GraphicsSettings>(true) : null;
            GameObject src = gs != null && F_gpRoot != null ? F_gpRoot.GetValue(gs) as GameObject : null;
            Button sl = gs != null && F_gpLeft != null ? F_gpLeft.GetValue(gs) as Button : null, sr = gs != null && F_gpRight != null ? F_gpRight.GetValue(gs) as Button : null;
            if (src == null || sl == null || sr == null) { if (!stepLogged) { stepLogged = true; Plugin.Log.LogWarning("Settings window: the game's preset arrows were not found"); } return false; }
            GameObject copy = UnityEngine.Object.Instantiate(src, into, false);
            copy.name = "cp_stepper" + i; copy.SetActive(true);
            Transform l = copy.transform.Find(PathIn(sl.transform, src.transform)), rt = copy.transform.Find(PathIn(sr.transform, src.transform));
            Button bl = l != null ? l.GetComponent<Button>() : null, br = rt != null ? rt.GetComponent<Button>() : null;
            if (bl == null || br == null) { UnityEngine.Object.Destroy(copy); return false; }
            r.lits.Add(Lit.Of(bl)); r.lits.Add(Lit.Of(br));   /* both arrows light for the focused row, as the game's own arrows do */
            Row rr = r;
            bl.onClick = new Button.ButtonClickedEvent(); bl.onClick.AddListener(delegate { Set(rr, (rr.index() - 1 + rr.shown.Length) % rr.shown.Length); });
            br.onClick = new Button.ButtonClickedEvent(); br.onClick.AddListener(delegate { Set(rr, (rr.index() + 1) % rr.shown.Length); });
            foreach (Selectable s in copy.GetComponentsInChildren<Selectable>(true)) { Navigation nav = s.navigation; nav.mode = Navigation.Mode.None; s.navigation = nav; }
            // its texts: its own yellow name hidden, the first other one is the value
            string parts = "";
            foreach (TMP_Text x in copy.GetComponentsInChildren<TMP_Text>(true))
            {
                parts += " '" + x.text + "'(" + x.fontSize + ")";
                if (x.transform.IsChildOf(bl.transform) || x.transform.IsChildOf(br.transform)) continue;
                Color c = x.color;
                // its own name and its description line hidden: only the value (its 'Value' text) stays
                // the value: the white text the game shows 'Value' in (0.71.1 looked for it by its object's name and hid it)
                Color xc = x.color;
                bool yellowName = xc.r > 0.8f && xc.g > 0.45f && xc.g < 0.85f && xc.b < 0.35f;
                string xt = (x.text ?? "").ToLowerInvariant(), xn = x.name.ToLowerInvariant();
                bool description = xt.Contains("description") || xn.Contains("description") || xn.Contains("desc");
                if (r.value == null && !yellowName && !description && (xt == "value" || xn.Contains("value") || x.fontSize >= 15f)) { r.value = x; continue; }
                x.gameObject.SetActive(false);
            }
            // no game text re-localizing it: the row writes its value itself
            foreach (Component c in copy.GetComponentsInChildren<Component>(true)) if (c != null && c.GetType().Name == "Localize") UnityEngine.Object.Destroy(c);
            r.stepper = true;
            // place: anchored by its top right, then moved so its right arrow ends 44 in from the window's right edge,
            // centred on the row
            RectTransform crt = (RectTransform)copy.transform;
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1f, 1f);
            crt.anchoredPosition = new Vector2(-44f, y);
            Canvas.ForceUpdateCanvases();
            Vector3[] q = new Vector3[4], pq = new Vector3[4];
            ((RectTransform)br.transform).GetWorldCorners(q); panel.GetWorldCorners(pq);
            float k = panel.lossyScale.x;
            float wantRight = pq[2].x - 44f * k, rowMid = pq[1].y + (y - RowH * 0.5f) * k;
            copy.transform.position += new Vector3(wantRight - q[2].x, rowMid - (q[0].y + q[1].y) * 0.5f, 0f);
            if (!stepLogged)
            {
                stepLogged = true;
                string comps = ""; foreach (Component c in copy.GetComponentsInChildren<Component>(true)) if (c != null) comps += " " + c.GetType().Name;
                Plugin.Log.LogInfo("Settings window: copied the game's preset arrows '" + src.name + "' (" + crt.rect.size + "), texts:" + parts + " | parts:" + comps);
            }
            return true;
        }

        // A button row's button (0.91.0): the game's own dialog button (the Quit dialog's, as the pause menu's Settings
        // button), its name the row's one value, its right edge 44 in from the window's, centred on the row. Not clickable:
        // A presses it through the row, and its own focus look shows on the focused row.
        static bool btnRowLogged;
        static bool RowButton(Row r, int i, float y)
        {
            Menu m = Menu.instance;
            Button src = null;
            if (m != null) foreach (Transform d in new Transform[] { m.m_quitDialog, m.m_logoutDialog })
                {
                    if (d == null || src != null) continue;
                    Button[] all = d.GetComponentsInChildren<Button>(true);
                    if (all.Length > 0) src = all[0];
                }
            if (src == null && m != null) src = m.m_continueButton;
            if (src == null) { if (!btnRowLogged) { btnRowLogged = true; Plugin.Log.LogWarning("Settings window: no game button to copy for '" + r.name + "'"); } return false; }
            GameObject copy = UnityEngine.Object.Instantiate(src.gameObject, into, false);
            copy.name = "cp_button" + i; copy.SetActive(true);
            foreach (Component c in copy.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (n == "UIGamePad" || n == "UIInputHint" || n == "Localize") UnityEngine.Object.Destroy(c);
            }
            Button b = copy.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            Navigation nav = b.navigation; nav.mode = Navigation.Mode.None; b.navigation = nav;
            r.lits.Add(Lit.Of(b)); b.interactable = true;
            TMP_Text label = null;
            foreach (TMP_Text x in copy.GetComponentsInChildren<TMP_Text>(true)) { if (label == null && x.gameObject.activeSelf) label = x; else x.gameObject.SetActive(false); }
            if (label != null) { label.enableAutoSizing = false; label.enableWordWrapping = false; label.text = r.shown[0]; label.alignment = TextAlignmentOptions.Center; }
            RectTransform crt = (RectTransform)copy.transform;
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1f, 1f);
            crt.sizeDelta = new Vector2(180f, Mathf.Clamp(crt.rect.height, 36f, RowH));
            crt.anchoredPosition = new Vector2(-44f, y - (RowH - crt.sizeDelta.y) * 0.5f);
            if (label != null) { RectTransform lt = label.rectTransform; lt.anchorMin = Vector2.zero; lt.anchorMax = Vector2.one; lt.offsetMin = Vector2.zero; lt.offsetMax = Vector2.zero; }
            if (!btnRowLogged) { btnRowLogged = true; Plugin.Log.LogInfo("Settings window: copied the game's button '" + src.name + "' for '" + r.name + "' (" + crt.sizeDelta + ")"); }
            return true;
        }

        static void Look(TMP_Text x, TMP_Text like) { if (x == null || like == null) return; x.fontSharedMaterial = like.fontSharedMaterial; x.fontStyle = like.fontStyle; }

    }
    // ---------- first-launch HUD layout pick (0.91.0, Rob) ----------
    // The first time the player is in a world on the controller, a plaque with the mod's picture sits at the bottom centre
    // ("Controller Support Plus" / Y Choose your HUD layout). It waits until the player can act (a new world's landing is
    // over) and has stood free for 2 s; in an older world that is at once. While it is up, Y opens the pick instead of the
    // inventory. Step 1: three cards, Vanilla / Bottom HUD / Action RPG, each the screen drawn as empty coloured boxes
    // (red health, yellow stamina, brown food, grey map, white hotbar). Step 2: the hotbar, Cross or Row, drawn on the
    // layout just picked. Left and right move, A goes on and confirms, B goes back (on step 1: later, the plaque stays).
    // Confirming sets the same two settings the Settings window does, the plaque goes for good (a flag in
    // internal-settings.cfg) and Y is the inventory again. The Settings window's top row opens the same pick again.
    // Only the HUD layout settings change; nothing in the world does.
    static class HudPick
    {
        public static bool Open, TakesY;
        static bool fromSettings, releaseWait;
        static int step, sel, bar, closedFrame = -1;
        static float freeSince = -1f;
        static GameObject plaque, root;
        static RectTransform panel, cards;
        static TMP_Text title, stepText, note;
        static readonly string[] Presets = { "Vanilla", "BottomHUD", "ActionRPG" };
        static readonly string[] PresetNames = { "Vanilla", "Bottom HUD", "Action RPG" };
        static readonly string[] PresetLines = { "The game's own HUD. Hotbar bottom right.", "Health bottom left, hotbar bottom centre, map bottom right.", "Health top left, hotbar bottom left, map bottom right." };
        static readonly string[] Bars = { "Cross", "CompactCross", "Row" };   /* the hotbar's three layouts (Compact cross added in 0.91.1) */
        static readonly string[] BarNames = { "Cross", "Compact cross", "Row" };
        static readonly string[] BarLines = { "Two crosses: the D-pad, and LT with the face buttons.", "The same two crosses, shorter.", "Two rows of four, side by side." };

        /* dev tool (0.91.1): the Settings window's top row in the developer build. Every setting back to its default, the
           windows and menus closed, and the plaque comes back after its usual 2 s, as on a first install. */
#if DEV
        static readonly MethodInfo M_MenuHide = AccessTools.Method(typeof(Menu), "Hide");
        public static void DevReset()
        {
            ModSettings.Close();
            Close();
            if (Menu.instance != null && Menu.IsVisible() && M_MenuHide != null) { try { M_MenuHide.Invoke(Menu.instance, null); } catch (Exception e) { Plugin.Log.LogWarning("Dev reset: menu not closed: " + e.Message); } }
            if (InventoryGui.instance != null && InventoryGui.IsVisible()) InventoryGui.instance.Hide();
            Plugin.ResetAll();
            freeSince = -1f; shownOnce = false;   /* as on a first install: the 2 s wait again */
        }
#endif
        public static bool Blocking { get { return Open || Time.frameCount == closedFrame || releaseWait; } }

        /* why: what holds the plaque back, written to the log when it changes, so a plaque that does not come back can be traced */
        static bool PlaqueWanted(out string why)
        {
            why = "";
            if (Plugin.HudPickSeen == null || Plugin.HudPickSeen.Value) { why = "done"; return false; }
            if (!Plugin.Enabled.Value || !ZInput.IsGamepadActive()) { why = "not on the controller"; return false; }
            Player p = Player.m_localPlayer;
            if (p == null) { why = "no player"; return false; }
            if (p.IsDead()) { why = "dead"; return false; }
            if (p.InIntro()) { why = "landing"; return false; }
            if (Hud.instance == null || Hud.IsUserHidden()) { why = "HUD hidden"; return false; }
            if (Hud.instance.m_rootObject == null || !Hud.instance.m_rootObject.activeInHierarchy) { why = "HUD switched off"; return false; }
            if (InventoryGui.IsVisible()) { why = "bag"; return false; }
            if (Menu.IsVisible()) { why = "menu"; return false; }
            if (Minimap.IsOpen()) { why = "map"; return false; }
            if (ModSettings.Open || Open) { why = "a window"; return false; }
            if (TextInput.IsVisible() || StoreGui.IsVisible()) { why = "text input or trader"; return false; }
            if (Chat.instance != null && Chat.instance.HasFocus()) { why = "chat"; return false; }
            return true;
        }

        public static void Tick()
        {
            if (releaseWait && !Core.RawHeld("JoyButtonA") && !Core.RawHeld("JoyButtonB") && !Core.RawHeld("JoyButtonY")) releaseWait = false;
            BodyRetry();   /* the body text look, until it is found */
            MeasureBody();   /* the controller picture's labels, measured on screen while the pause menu shows them */
            if (Open) { Drive(); TakesY = false; plaqueK = 0f; Slide(); if (Open) { SplashTick();
#if DEV
                CapturePick();
#endif
            } return; }
            string why;
            bool free = PlaqueWanted(out why);
            if (why != lastWhy) { lastWhy = why; if (Plugin.HudPickSeen != null && !Plugin.HudPickSeen.Value) Plugin.Log.LogInfo("HUD pick: plaque " + (free ? "free to show" : "held back: " + why)); }
            if (!free) freeSince = -1f; else if (freeSince < 0f) freeSince = Time.unscaledTime;
            // the first time, it waits 2 s once the player is free (a new world's landing); after that, it comes straight
            // back whenever what covered it goes (the map, the bag, a menu) (0.91.2: it waited 2 s every time)
            bool show = free && (shownOnce || Time.unscaledTime - freeSince >= 2f);
            if (show) shownOnce = true;
            if (show && plaque == null) BuildPlaque();
            plaqueK = Mathf.MoveTowards(plaqueK, show ? 1f : 0f, Time.unscaledDeltaTime / SlideTime);
            Slide();
#if DEV
            if (plaqueK >= 1f) CapturePlaque();
#endif
            TakesY = show && !Core.RawHeld("JoyLTrigger");   /* LT + Y stays a Quick slot */
            if (TakesY && Core.RawDown("JoyButtonY") && InventoryGui.instance != null) Show(false);
        }
        static string lastWhy; static bool shownOnce;
#if DEV
        /* screenshots of the plaque and both steps of the pick, in the developer build (empty in the shared one) */
        static void CapturePlaque() { Shots.Want("hud-pick-plaque"); }
        static void CapturePick() { Shots.Want(step == 0 ? "hud-pick-splash" : step == 1 ? "hud-pick-hotbar" : "hud-pick-layouts"); }
#endif
        // The plaque slides down 20 into its place at the top as it fades in, eased, and back up as it fades out (0.91.2, Rob; from the bottom until 0.91.3)
        const float SlideTime = 0.25f, SlideFrom = 20f, PlaqueY = 28f;
        static float plaqueK; static CanvasGroup plaqueFade;
        static void Slide()
        {
            if (plaque == null) return;
            bool on = plaqueK > 0.001f;
            if (plaque.activeSelf != on) plaque.SetActive(on);
            if (!on) return;
            float e = plaqueK * plaqueK * (3f - 2f * plaqueK);
            if (plaqueFade != null) plaqueFade.alpha = e;
            /* as wide as its texts, again whenever their size changes (the Body size is measured later on) */
            float pw = PlaqueWidth();
            if (plaquePanel != null && Mathf.Abs(plaquePanel.sizeDelta.x - pw) > 0.5f) { plaquePanel.sizeDelta = new Vector2(pw, PlaqueH); ((RectTransform)plaque.transform).sizeDelta = plaquePanel.sizeDelta; }
            ((RectTransform)plaque.transform).anchoredPosition = new Vector2(0f, -PlaqueY + SlideFrom * (1f - e));   /* from above, down into place at the top (0.91.3, Rob) */
        }

        // ----- the body text look (0.91.2, Rob): the labels on the pause menu's controller picture -----
        // Every body text the mod adds from here on uses it: font, material, colour and style copied, the size matched on
        // screen (the picture's canvas and the HUD's are scaled apart). Headers keep the Settings screen's yellow.
        // 0.91.11: looked for in more places (the pause menu, the Settings screen, any loaded copy of the picture), tried
        // again every second until found, and every text styled before then is given the look the moment it is found
        // (in 0.91.10 the pause menu had not been opened yet, the look was not found and the texts kept a plain font).
        static TMP_Text bodyLike; static float bodyUnits, bodyNextTry;
        static readonly List<TMP_Text> bodyWaiting = new List<TMP_Text>();
        public static void BodyLook(TMP_Text x)
        {
            if (x == null) return;
            if (bodyLike == null) FindBody();
            if (bodyLike == null) { if (!bodyWaiting.Contains(x)) bodyWaiting.Add(x); return; }
            BodyOn(x);
        }
        public static void BodyRetry() { if (bodyLike == null && bodyWaiting.Count > 0) FindBody(); }
        /* the Body size on screen, in pixels: as measured on the pause menu's controller picture, else the first guess */
        public static float BodyPx() { float u = Plugin.BodySeen != null && Plugin.BodySeen.Value > 0.05f ? Plugin.BodySeen.Value : bodyUnits; return u * TextStyle.HudScale(); }   /* kept in HUD units, drawn at this resolution (0.92.30) */
        // While the pause menu shows its controller picture, its labels are measured on screen and the size remembered
        // (internal BodyScreenSize), so Body text matches them wherever it is drawn (0.91.12, Rob). Once per menu opening.
        static bool bodyMeasured;
        static void MeasureBody()
        {
            Menu m = Menu.instance;
            GameObject gp = m != null ? m.m_gamepadRoot : null;
            if (gp == null || !Menu.IsVisible() || !gp.activeInHierarchy) { bodyMeasured = false; return; }
            if (bodyMeasured) return;
            Type mapType = AccessTools.TypeByName("Valheim.SettingsGui.GamepadMap") ?? AccessTools.TypeByName("GamepadMap");
            TMP_Text live = mapType != null ? MapLabel(gp.GetComponentInChildren(mapType, false), mapType) : null;
            if (live == null || !live.gameObject.activeInHierarchy) return;
            float px = live.fontSize * Mathf.Abs(live.transform.lossyScale.y);
            if (px < 4f) return;
            bodyMeasured = true;
            if (bodyLike == null || !bodyLike.gameObject.scene.IsValid()) { bodyLike = live; foreach (TMP_Text w in bodyWaiting) if (w != null) BodyOn(w); bodyWaiting.Clear(); }
            float hudK = TextStyle.HudScale();
            float bu = hudK > 0.0001f ? px / hudK : 0f;
            if (bu > 0f && Mathf.Abs(Plugin.BodySeen.Value - bu) > 0.05f)
            {
                Plugin.BodySeen.Value = bu;
                Plugin.Log.LogInfo("Text styles: the controller picture's labels are " + bu.ToString("0.00") + " HUD units, " + px.ToString("0.0") + " px on screen ('" + live.text + "', " + live.fontSize + "); Body drawn at that size");
            }
        }
        static void BodyOn(TMP_Text x)
        {
            x.font = bodyLike.font; x.fontSharedMaterial = bodyLike.fontSharedMaterial; x.fontStyle = bodyLike.fontStyle;
            x.color = bodyLike.color; x.characterSpacing = bodyLike.characterSpacing; x.enableAutoSizing = false;
            /* sized on screen by its BodySize, from the picture labels measured on screen (0.91.12) */
            BodySize bs = x.GetComponent<BodySize>(); if (bs == null) bs = x.gameObject.AddComponent<BodySize>(); bs.Fit();
        }
        static TMP_Text MapLabel(Component map, Type mapType)
        {
            FieldInfo f = AccessTools.Field(mapType, "joyButton0");
            object lab = f != null && map != null ? f.GetValue(map) : null;
            PropertyInfo p = lab != null ? AccessTools.Property(lab.GetType(), "Label") : null;
            return p != null ? p.GetValue(lab, null) as TMP_Text : null;
        }
        static bool bodyMissLogged;
        static void FindBody()
        {
            if (Time.unscaledTime < bodyNextTry) return;
            bodyNextTry = Time.unscaledTime + 1f;
            string from = "";
            try
            {
                Type mapType = AccessTools.TypeByName("Valheim.SettingsGui.GamepadMap") ?? AccessTools.TypeByName("GamepadMap");
                Menu m = Menu.instance;
                GameObject gp = m != null ? m.m_gamepadRoot : null;
                if (mapType != null && gp != null) { bodyLike = MapLabel(gp.GetComponentInChildren(mapType, true), mapType); from = "the pause menu"; }
                if (bodyLike == null && mapType != null && m != null && m.m_settingsPrefab != null) { bodyLike = MapLabel(m.m_settingsPrefab.GetComponentInChildren(mapType, true), mapType); from = "the Settings screen"; }
                if (bodyLike == null && mapType != null)
                    foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(mapType)) { bodyLike = MapLabel(o as Component, mapType); if (bodyLike != null) { from = "a loaded controller picture"; break; } }
                if (bodyLike == null) { if (!bodyMissLogged) { bodyMissLogged = true; Plugin.Log.LogInfo("Body text: the controller picture's labels are not loaded yet; trying again each second"); } return; }
                // its size on screen: from a picture on screen when there is one, else as the HUD canvas would draw it
                float k = Mathf.Abs(bodyLike.transform.lossyScale.y);
                if (k < 0.0001f || !bodyLike.gameObject.scene.IsValid()) k = TextStyle.HudScale();
                float hk = TextStyle.HudScale();
                bodyUnits = hk > 0.0001f ? bodyLike.fontSize * k / hk : bodyLike.fontSize;   /* in HUD units (0.92.30) */
                Plugin.Log.LogInfo("Body text: from " + from + ", '" + bodyLike.text + "' " + (bodyLike.font != null ? bodyLike.font.name : "-") + " " + bodyLike.fontSize + " (" + bodyUnits.ToString("0.00") + " HUD units) " + bodyLike.color + " " + bodyLike.fontStyle + "; " + bodyWaiting.Count + " waiting texts styled");
                foreach (TMP_Text w in bodyWaiting) if (w != null) BodyOn(w);
                bodyWaiting.Clear();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Body text: " + e.Message); bodyLike = null; }
        }
        /* a button glyph at the size the button hints draw theirs */
        static string HintGlyph(string button) { return "<size=" + Plugin.GlyphSize.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ">" + Hints.Glyph(button) + "</size>"; }

        // ----- the plaque: the split window's wood and frames, the mod's picture, its name and the prompt -----
        static void BuildPlaque()
        {
            Hud h = Hud.instance; InventoryGui gui = InventoryGui.instance;
            if (h == null || h.m_rootObject == null || gui == null) return;
            RectTransform pp; TMP_Text t;
            plaque = Favorites.Shell(gui, "ControllerPro_HudPickPlaque", new Vector2(420f, PlaqueH), "", out pp, out t);
            plaqueFade = plaque.AddComponent<CanvasGroup>(); plaqueFade.blocksRaycasts = false; plaqueFade.interactable = false;
            plaque.transform.SetParent(h.m_rootObject.transform, false);   /* part of the HUD: hidden with it */
            foreach (Transform ch in plaque.transform) if (ch != pp) ch.gameObject.SetActive(false);   /* no dim */
            /* 0.92.14 (Rob: a second ground showed for a moment as it faded in): a window layer wholly covered by an opaque layer
               drawn over it is never seen, except through the fade; it is left out */
            HideCovered(pp, t);
            Image own = plaque.GetComponent<Image>(); if (own != null) own.enabled = false;
            if (t != null) t.gameObject.SetActive(false);
            RectTransform rr = (RectTransform)plaque.transform;
            rr.anchorMin = rr.anchorMax = rr.pivot = new Vector2(0.5f, 1f); rr.sizeDelta = pp.sizeDelta; rr.anchoredPosition = new Vector2(0f, -PlaqueY);   /* top centre since 0.91.3 (Rob); bottom centre in 0.91.1-0.91.2 */
            pp.anchorMin = pp.anchorMax = pp.pivot = new Vector2(0.5f, 0.5f); pp.anchoredPosition = Vector2.zero;
            plaque.SetActive(true);   /* on while it is built, so its texts can be measured */
            TMP_Text like = gui.m_weight != null ? gui.m_weight : gui.m_containerName;
            Vector2 l = new Vector2(0f, 0.5f);
            Texture2D icon = Icon();
            /* tighter since 0.91.2 (Rob): 8 around the picture, 14 after the text (14 and 22 around a 68 picture before) */
            float x = 8f;
            if (icon != null)
            {
                GameObject ig = Hints.New("icon", pp);
                RawImage ri = ig.AddComponent<RawImage>(); ri.texture = icon; ri.raycastTarget = false;
                Hints.Place(ig, l, l, l, new Vector2(x, 0f), new Vector2(PlaqueH - 16f, PlaqueH - 16f));
                x += PlaqueH - 16f + 10f;
            }
            TMP_Text name = Hints.Text(Hints.New("name", pp), like, 18f, Color.white, TextAlignmentOptions.BottomLeft);
            Hints.Place(name.gameObject, l, l, l, new Vector2(x, 15f), new Vector2(320f, 24f));
            name.text = "Controller Support Plus";
            TextStyle.Apply(name, TextStyle.Kind.Header);   /* a Header, the same size as everywhere (18 here until 0.91.8) */
            TMP_Text prompt = Hints.Text(Hints.New("prompt", pp), like, 18f, Color.white, TextAlignmentOptions.TopLeft);
            Hints.Place(prompt.gameObject, l, l, l, new Vector2(x, -15f), new Vector2(320f, 26f));
            TextStyle.Apply(prompt, TextStyle.Kind.Body);
            prompt.text = HintGlyph("JoyButtonY") + "  Begin Setup";
            plaqueName = name; plaquePrompt = prompt; plaqueText0 = x; plaquePanel = pp;
            float w = PlaqueWidth();
            pp.sizeDelta = new Vector2(w, PlaqueH); rr.sizeDelta = pp.sizeDelta;
            plaque.SetActive(false);
            Plugin.Log.LogInfo("HUD pick: plaque built, " + w + " wide, picture " + (icon != null ? "loaded" : "not found"));
        }
        const float PlaqueH = 78f;   /* 78 since 0.91.3: more air between the title and the prompt (68 in 0.91.2, 92 before) */
        static TMP_Text plaqueName, plaquePrompt; static float plaqueText0; static RectTransform plaquePanel;
        static float PlaqueWidth() { return plaqueName == null || plaquePrompt == null ? 0f : plaqueText0 + Mathf.Max(plaqueName.GetPreferredValues(plaqueName.text).x, plaquePrompt.GetPreferredValues(plaquePrompt.text).x) + 14f; }
        static void HideCovered(RectTransform pp, TMP_Text t)
        {
            List<Image> layers = new List<Image>();
            foreach (Transform ch in pp) { if (t != null && ch == t.transform) continue; Image im = ch.GetComponent<Image>(); if (im != null && ch.gameObject.activeSelf) layers.Add(im); }
            for (int i = 0; i < layers.Count; i++)
                for (int j = i + 1; j < layers.Count; j++)
                {
                    Image top = layers[j]; RectTransform a = layers[i].rectTransform, b = top.rectTransform;
                    bool opaque = top.color.a > 0.99f && (top.type != Image.Type.Sliced || top.fillCenter);
                    bool covers = b.anchorMin == a.anchorMin && b.anchorMax == a.anchorMax && b.sizeDelta.x >= a.sizeDelta.x && b.sizeDelta.y >= a.sizeDelta.y;
                    if (opaque && covers) { layers[i].gameObject.SetActive(false); Plugin.Log.LogInfo("HUD pick: plaque layer '" + layers[i].name + "' is covered by '" + top.name + "': left out"); break; }
                }
        }
        static Texture2D icon; static bool iconTried;
        public static Texture2D Icon()   /* also the pause menu's settings button (1.0.0) */
        {
            if (iconTried) return icon;
            iconTried = true;
            try
            {
                /* the picture built into the DLL (V1): the right one for the build, wherever the DLL was installed. The
                   icon.png beside it only if that is missing. */
                byte[] png = null;
                using (System.IO.Stream s = typeof(Plugin).Assembly.GetManifestResourceStream("ControllerPro.icon.png"))
                    if (s != null) { png = new byte[s.Length]; int got = 0; while (got < png.Length) { int n = s.Read(png, got, png.Length - got); if (n <= 0) break; got += n; } }
                if (png == null)
                {
                    string f = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "icon.png");
                    if (!System.IO.File.Exists(f)) return null;
                    png = System.IO.File.ReadAllBytes(f);
                }
                icon = new Texture2D(2, 2, TextureFormat.RGBA32, true);   /* mipmapped, so it stays smooth drawn small (1.0.0) */
                if (!ImageConversion.LoadImage(icon, png)) icon = null;
            }
            catch (Exception e) { Plugin.Log.LogWarning("HUD pick: picture not loaded: " + e.Message); icon = null; }
            return icon;
        }

        // ----- the pick -----
        public static void OpenFromSettings() { Show(true); }
        static void Show(bool settings)
        {
            InventoryGui gui = InventoryGui.instance; Hud h = Hud.instance;
            if (gui == null || h == null || h.m_rootObject == null) return;
            if (root == null) Build(gui);
            if (root == null) return;
            Transform to = h.m_rootObject.transform.parent;
            if (root.transform.parent != to)
            {
                root.transform.SetParent(to, false);
                RectTransform rr = (RectTransform)root.transform;
                rr.anchorMin = Vector2.zero; rr.anchorMax = Vector2.one; rr.pivot = new Vector2(0.5f, 0.5f); rr.anchoredPosition = Vector2.zero; rr.sizeDelta = Vector2.zero;
            }
            fromSettings = settings;
            if (settings) ModSettings.HideForPick(true);
            Snapshot();
            Open = true; step = settings ? 1 : 0; openedFrame = Time.frameCount;   /* Rerun setup skips the splash (0.92.20, Rob) */
            // step 1 is the hotbar, step 2 the layout (0.91.15, Rob); each starts on what is in use now
            bar = Math.Max(0, Array.IndexOf(Bars, Plugin.HotbarStyle.Value));
            sel = Math.Max(0, Array.IndexOf(Presets, Plugin.HudPreset.Value));
            root.SetActive(true); root.transform.SetAsLastSibling();
            panel.position = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, panel.position.z);
            Fill();
            Plugin.Log.LogInfo("HUD pick: opened" + (settings ? " from the Settings window" : " from the plaque"));
        }
        // ----- live preview (0.91.15, Rob) -----
        // Each move shows its choice on the HUD at once: on the hotbar step the hotbar changes, on the layout step the
        // layout (with the hotbar picked) too. Everything the pick can change is noted when it opens; leaving without
        // confirming (B on the first step, or the pick closing on its own) puts all of it back. Only the last A keeps it.
        static ConfigEntryBase[] Touched()
        {
            return new ConfigEntryBase[] { Plugin.HudPreset, Plugin.HotbarStyle, Plugin.ResourceStyle, Plugin.BottomBarSide, Plugin.RpgMap, Plugin.ShowMinimap, Plugin.BuffStyle, Plugin.BuffPos, Plugin.FeedPos, Plugin.EquipProgress };
        }
        static object[] before;
        static void Snapshot()
        {
            ConfigEntryBase[] t = Touched();
            before = new object[t.Length];
            for (int i = 0; i < t.Length; i++) before[i] = t[i] != null ? t[i].BoxedValue : null;
        }
        static void Revert()
        {
            if (before == null) return;
            ConfigEntryBase[] t = Touched();
            // the preset first: its own change handling resets the styles, then each one back as it was
            for (int i = 0; i < t.Length; i++) if (t[i] != null && !Equals(t[i].BoxedValue, before[i])) t[i].BoxedValue = before[i];
            for (int i = 1; i < t.Length; i++) if (t[i] != null && !Equals(t[i].BoxedValue, before[i])) t[i].BoxedValue = before[i];
        }
        static void Preview()
        {
            // the hotbar step shows the hotbar on the layout in use; the layout step adds the layout, the hotbar kept on top
            // the splash (step 0) shows nothing new: the HUD as it was
            string preset = step == 2 ? Presets[sel] : before != null ? (string)before[0] : Plugin.HudPreset.Value;
            string hot = step == 0 && before != null ? (string)before[1] : Bars[bar];
            if (Plugin.HudPreset.Value != preset) Plugin.HudPreset.Value = preset;
            if (Plugin.HotbarStyle.Value != hot) Plugin.HotbarStyle.Value = hot;
        }
        static void Close()
        {
            if (Open) { closedFrame = Time.frameCount; releaseWait = true; }
            Open = false;
            if (root != null) root.SetActive(false);
            if (fromSettings) ModSettings.HideForPick(false);
            fromSettings = false;
        }
        /* leaving without the last A: everything back as it was */
        static void Cancel()
        {
            Revert();
            before = null;
            Plugin.Log.LogInfo("HUD pick: left without confirming, the HUD put back as it was");
            Close();
        }
        public static void LateCheck()
        {
            /* the pick goes away (no change) when the controller is put down, or the screen it was opened over goes */
            if (!Open) return;
            bool ok = Plugin.Enabled.Value && ZInput.IsGamepadActive() && Player.m_localPlayer != null && (fromSettings ? ModSettings.Open : !Menu.IsVisible() && !InventoryGui.IsVisible());
            if (!ok) Cancel();
        }

        static int openedFrame = -1;
        static void Drive()
        {
            if (Time.frameCount == openedFrame) return;   /* the press that opened it (A in the Settings window) is not also a step */
            int d = Core.RawDown("JoyDPadLeft") || Core.RawDown("JoyLStickLeft") ? -1 : Core.RawDown("JoyDPadRight") || Core.RawDown("JoyLStickRight") ? 1 : 0;
            bool a = Core.RawDown("JoyButtonA"), b = Core.RawDown("JoyButtonB");
            // 0 the splash (Y Begin Setup), 1 the hotbar, 2 the layout (0.92.0); B steps back, and leaves from the splash
            if (step == 0)
            {
                // the splash (0.92.19, Rob): A moves on to the next card, left and right walk the cards; Y begins, once the Begin
                // Setup button shows (from the last card on); B leaves. The button is never focused or walked to.
                if (Core.RawDown("JoyButtonY") && beginShown) { step = 1; Preview(); Fill(); }
                else if (d != 0) SplashGo(splashCur + d);
                else if (a && splashCur < 3) SplashGo(splashCur + 1);
                else if (b) Cancel();
                return;
            }
            if (d != 0) { if (step == 1) bar = (bar + d + 3) % 3; else sel = (sel + d + 3) % 3; Preview(); Fill(); }
            else if (a && step < 2) { step++; Preview(); Fill(); }
            else if (a) Confirm();
            else if (b && step > (fromSettings ? 1 : 0)) { step--; Preview(); Fill(); }   /* back: the layout in use again on the hotbar step, nothing new on the splash; from Rerun setup, B on the hotbar step leaves */
            else if (b) Cancel();
        }
        static void Confirm()
        {
            Preview();   /* what is on the cards is what stays */
            before = null;
            Plugin.HudPickSeen.Value = true;
            Plugin.Log.LogInfo("HUD pick: " + Presets[sel] + " with the " + Bars[bar] + " hotbar");
            /* finished from Rerun setup (1.0.4, Rob): back to play, not to the settings window it was opened from */
            bool wasSettings = fromSettings;
            fromSettings = false;
            Close();
            if (wasSettings) ModSettings.CloseToPlay();
            plaqueK = 0f; Slide();
            /* the game's own big centre message, as for resting (0.91.4, Rob): the setup is done */
            if (Player.m_localPlayer != null) Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Controller setup applied", 0, null, false);
        }

        /* more air between the parts (0.91.10, Rob): card gap 32 (20), sides 48 (40), cards 96 down (82), cards 270 tall (252) */
        const float CardW = 280f, CardH = 270f, CardGap = 14f, Side = 28f,   /* 0.92.18 (Rob): the splash's spacing (32 and 48 before) */ CardsTop = -96f, WireW = 256f, WireH = 144f, NoteH = 36f, NoteBottom = 28f, NoteGap = 24f;
        static void Build(InventoryGui gui)
        {
            TMP_Text t;
            root = Favorites.Shell(gui, "ControllerPro_HudPick", new Vector2(3f * CardW + 2f * CardGap + 2f * Side, -CardsTop + CardH + NoteGap + NoteH + NoteBottom), "Choose your HUD layout", out panel, out t);   /* no button hints line since 0.91.1 (Rob) */
            title = t;
            // the window's own art and dim, noted so the full-screen splash can put them away (0.92.1)
            windowArt.Clear(); windowDim.Clear();
            foreach (Transform ch in panel) if (t == null || ch != t.transform) windowArt.Add(ch.gameObject);
            foreach (Transform ch in root.transform) if (ch != panel) windowDim.Add(ch.gameObject);
            splashDim = Hints.New("splashDim", root.transform);
            Hints.Img(splashDim, null, new Color(0f, 0f, 0f, 0.75f));   /* the whole screen under 75% black over the game's blur (0.92.2, Rob; 50% without blur before) */
            Hints.Place(splashDim, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            splashDim.transform.SetAsFirstSibling(); splashDim.SetActive(false);
            splashBlur = null;   /* no blur (0.92.5, Rob took it out again); GameBlur is kept for later */
            if (splashBlur != null) { splashBlur.transform.SetAsFirstSibling(); splashBlur.SetActive(false); }
            root.SetActive(true);   /* built switched on, so its texts wake up with the game's font */
            TMP_Text like = gui.m_weight != null ? gui.m_weight : gui.m_containerName;
            Vector2 top = new Vector2(0.5f, 1f), bottom = new Vector2(0.5f, 0f);
            stepText = Hints.Text(Hints.New("step", panel), like, 16f, ModSettings.Grey(), TextAlignmentOptions.Center);
            Hints.Place(stepText.gameObject, top, top, top, new Vector2(0f, -50f), new Vector2(600f, 22f));
            cards = Hints.Place(Hints.New("cards", panel), top, top, top, new Vector2(0f, CardsTop), new Vector2(0f, CardH));
            /* the note sits on a band of the cards' black, across the window and fading out at both ends (0.91.10, Rob) */
            Image band = Hints.Img(Hints.New("noteBand", panel), FadeBand(), new Color(0f, 0f, 0f, 0.55f));
            Hints.Place(band.gameObject, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, NoteBottom), new Vector2(-24f, NoteH));
            noteBand = band.gameObject;
            note = Hints.Text(Hints.New("note", band.transform), like, 16f, ModSettings.Grey(), TextAlignmentOptions.Center);
            Hints.Place(note.gameObject, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            note.text = "You can change this any time in Controller Support Plus Settings.";
            /* the step line and the note: the Hint style, as the button guide's labels (0.91.8, Rob; Body in grey before) */
            TextStyle.Apply(stepText, TextStyle.Kind.Hint); TextStyle.Apply(note, TextStyle.Kind.Hint);
            // the bag slot's own focus look (its white frame with the light fill), copied once, laid over the focused card
            GameObject prefab = gui.m_playerGrid != null ? gui.m_playerGrid.m_elementPrefab : null;
            InventoryElement el = prefab != null ? prefab.GetComponent<InventoryElement>() : null;
            RectTransform slot = prefab != null ? prefab.transform as RectTransform : null;
            if (el != null && el.m_selected != null)
            {
                focusFx = UnityEngine.Object.Instantiate(el.m_selected, panel, false);
                focusFx.name = "cp_cardFocus"; focusFx.SetActive(false);
                foreach (Graphic g in focusFx.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                RectTransform src = el.m_selected.transform as RectTransform;
                if (src != null && slot != null && slot.rect.width > 1f) focusOver = new Vector2(src.rect.width - slot.rect.width, src.rect.height - slot.rect.height);
                Plugin.Log.LogInfo("HUD pick: the bag slot's focus frame for the cards (" + focusOver + " larger than the slot)");
            }
            else Plugin.Log.LogWarning("HUD pick: the bag slot's focus frame was not found");
            FindFrame();
            BuildSplashExtras(gui);   /* the splash's picture and its Begin Setup button (0.92.0) */
            root.SetActive(false);
        }

        // A white band that is solid in the middle and fades to nothing over its outer quarter at each end, tinted by the
        // Image's colour (black at 55% for the note).
        static Sprite fadeBand;
        static Sprite FadeBand()
        {
            if (fadeBand != null) return fadeBand;
            const int W = 256;
            Texture2D tx = new Texture2D(W, 4, TextureFormat.RGBA32, false);
            tx.wrapMode = TextureWrapMode.Clamp; tx.filterMode = FilterMode.Bilinear;
            Color32[] px = new Color32[W * 4];
            for (int x = 0; x < W; x++)
            {
                float a = Mathf.Clamp01(Mathf.Min(x, W - 1 - x) / (W * 0.25f));
                a = a * a * (3f - 2f * a);
                for (int y = 0; y < 4; y++) px[y * W + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
            tx.SetPixels32(px); tx.Apply(false, true);
            fadeBand = Sprite.Create(tx, new Rect(0, 0, W, 4), new Vector2(0.5f, 0.5f), 100f);
            return fadeBand;
        }

        // The game's Settings screen frames its groups in a thin yellow frame (Rob, 0.91.10). Found by looking through the
        // Settings screen for stretchable art that is drawn yellow, or named as a frame or border; its art, colour and
        // material are copied onto each card. Every candidate is written to the log once, so the right one can be picked.
        static Image frameSrc; static bool frameLooked;
        static void FindFrame()
        {
            if (frameLooked) return;
            frameLooked = true;
            try
            {
                Menu m = Menu.instance;
                GameObject prefab = m != null ? m.m_settingsPrefab : null;
                if (prefab == null) { Plugin.Log.LogWarning("HUD pick: the Settings screen is not loaded, no frame for the cards"); return; }
                // 0.91.11: only a frame that wraps a whole group: stretchable art at least 200 x 100, with an empty middle (or
                // drawn as a frame), never a control's small part (0.91.10 took a slider's handle and stretched it over the
                // cards). Every large stretchable piece is written to the log, so the right one can be named if this misses.
                // 0.91.12: the Settings screen's group panel itself ('TabContent', art panel_interior_bkg_128): its yellow edge is
                // part of its own art, so it is what wraps each card (the game has no separate frame piece)
                foreach (Image im in prefab.GetComponentsInChildren<Image>(true))
                    if (im.sprite != null && (im.name == "TabContent" || im.sprite.name.Contains("panel_interior"))) { frameSrc = im; break; }
                if (frameSrc != null) { Plugin.Log.LogInfo("HUD pick: card frame from the Settings screen's group panel '" + frameSrc.name + "' (" + frameSrc.sprite.name + ", " + frameSrc.type + ", border " + frameSrc.sprite.border + ")"); return; }
                string seen = ""; int best = 0, listed = 0;
                foreach (Image im in prefab.GetComponentsInChildren<Image>(true))
                {
                    if (im.sprite == null || im.type != Image.Type.Sliced || im.sprite.border.sqrMagnitude <= 0f) continue;
                    Rect r = im.rectTransform.rect;
                    if (r.width < 200f || r.height < 100f) continue;
                    Color c = im.color;
                    bool yellow = c.r > 0.6f && c.g > 0.35f && c.g < 0.9f && c.b < 0.45f && c.a > 0.3f;
                    string n = (im.name + " " + im.sprite.name).ToLowerInvariant();
                    bool named = n.Contains("frame") || n.Contains("border") || n.Contains("outline");
                    bool hollow = !im.fillCenter || named;
                    int score = (yellow ? 4 : 0) + (hollow ? 2 : 0) + (named ? 1 : 0);
                    if (listed < 30) { listed++; seen += " | " + im.name + " (" + im.sprite.name + ", " + (int)r.width + "x" + (int)r.height + ", " + c + ", centre " + (im.fillCenter ? "filled" : "empty") + ", score " + score + ")"; }
                    if (yellow && hollow && score > best) { best = score; frameSrc = im; }
                }
                Plugin.Log.LogInfo("HUD pick: Settings screen frame candidates:" + seen);
                Plugin.Log.LogInfo("HUD pick: card frame " + (frameSrc != null ? "copied from '" + frameSrc.name + "' (" + frameSrc.sprite.name + ")" : "not found: the cards have none (see the candidates above)"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("HUD pick: frame: " + e.Message); frameSrc = null; }
        }
        /* the cards' yellow frame on anything else (0.92.21: the Settings window's tab strip and content box) */
        public static void FrameOn(Transform t) { FindFrame(); CardFrame(t); }
        static void CardFrame(Transform card)
        {
            if (frameSrc == null) return;
            Image fr = Hints.Img(Hints.New("frame", card), frameSrc.sprite, frameSrc.color);
            fr.type = frameSrc.type; fr.material = frameSrc.material; fr.pixelsPerUnitMultiplier = frameSrc.pixelsPerUnitMultiplier; fr.fillCenter = frameSrc.fillCenter;
            Hints.Place(fr.gameObject, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        }

        // the cards for this step, drawn fresh
        static void Fill()
        {
            if (cards == null) return;
            if (focusFx != null) { focusFx.SetActive(false); focusFx.transform.SetParent(panel, false); }   /* kept: the old cards go */
            for (int i = cards.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(cards.GetChild(i).gameObject);
            InventoryGui gui = InventoryGui.instance;
            TMP_Text like = gui != null ? (gui.m_weight != null ? gui.m_weight : gui.m_containerName) : null;
            // steps since 0.92.0: 0 the splash, 1 the hotbar, 2 the layout
            if (step == 0) { FillSplash(like); return; }
            SplashParts(false);
            bool first = step == 2;   /* "first" is the layout cards: the last step (the hotbar comes first since 0.91.15) */
            int n = 3, on = first ? sel : bar;
            float total = n * CardW + (n - 1) * CardGap;
            panel.sizeDelta = new Vector2(total + 2f * Side, PickH);
            Vector2 tl = new Vector2(0f, 1f);
            for (int i = 0; i < n; i++)
            {
                bool focus = i == on;
                GameObject c = Hints.New("card" + i, cards);
                Hints.Place(c, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), tl, new Vector2(-total * 0.5f + i * (CardW + CardGap), 0f), new Vector2(CardW, CardH));
                /* the card's ground: plain square edges (0.91.0's rounded art was pixelated when stretched) */
                Image bg = Hints.Img(Hints.New("bg", c.transform), null, new Color(0f, 0f, 0f, 0.2f));   /* 20% black, as the splash's cards (0.92.18; 55% before) */
                Hints.Place(bg.gameObject, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
                CardFrame(c.transform);   /* the Settings screen's yellow frame around every card (0.91.10, Rob) */
                if (focus && focusFx != null)
                {
                    /* the bag slot's own focus frame over the focused card, with its overhang (0.91.1, Rob) */
                    RectTransform frt = (RectTransform)focusFx.transform;
                    frt.SetParent(c.transform, false);
                    frt.anchorMin = frt.anchorMax = frt.pivot = new Vector2(0.5f, 0.5f);
                    frt.anchoredPosition = Vector2.zero; frt.sizeDelta = new Vector2(CardW, CardH) + focusOver;
                    focusFx.SetActive(true);
                }
                if (first) Wire(c.transform, Presets[i], Bars[bar], focus);   /* each layout with the hotbar just picked */
                else Bar(c.transform, Bars[i], focus);
                // the showcase frame as on the splash (0.92.18, Rob): black at 50% across the whole width of the card, from its top
                // edge; the picture drawn in its middle as before (its own ground left out), the card's size unchanged
                {
                    Transform pic = c.transform.Find("screen");
                    Image frame = Hints.Img(Hints.New("frame", c.transform), null, new Color(0f, 0f, 0f, 0.5f));
                    Hints.Place(frame.gameObject, tl, tl, tl, Vector2.zero, new Vector2(CardW, WireH + 12f));
                    frame.transform.SetSiblingIndex(pic != null ? pic.GetSiblingIndex() : 1);
                    if (pic != null)
                    {
                        Image own = pic.GetComponent<Image>(); if (own != null) own.enabled = false;
                        RectTransform prt = (RectTransform)pic; prt.anchoredPosition = new Vector2(12f, -6f); SnapToPixel(prt);
                    }
                }
                /* the header in the Settings screen's yellow, the line under it in the body text look (0.91.2, Rob) */
                TMP_Text nm = Hints.Text(Hints.New("name", c.transform), like, 20f, Color.white, TextAlignmentOptions.TopLeft);
                Hints.Place(nm.gameObject, tl, tl, tl, new Vector2(12f, -WireH - 30f), new Vector2(WireW, 28f));   /* 30 under the picture (22 before) */
                TextStyle.Apply(nm, TextStyle.Kind.Header);
                if (!focus) nm.color = new Color(nm.color.r, nm.color.g, nm.color.b, 0.6f);
                nm.text = first ? PresetNames[i] : BarNames[i];
                TMP_Text ln = Hints.Text(Hints.New("line", c.transform), like, 16f, Color.white, TextAlignmentOptions.TopLeft);
                Hints.Place(ln.gameObject, tl, tl, tl, new Vector2(12f, -WireH - 56f), new Vector2(WireW, 60f));   /* closer under the header (0.91.12, Rob; 64 in 0.91.10-0.91.11) */
                TextStyle.Apply(ln, TextStyle.Kind.Body);
                ln.enableWordWrapping = true;
                ln.text = first ? PresetLines[i] : BarLines[i];
            }
            if (title != null) title.text = first ? "Choose your HUD layout" : "Choose your hotbar";
            stepText.text = "Step " + step + " of 2" + (first ? "  ·  " + BarNames[bar] + " hotbar" : "");
        }
        static GameObject focusFx; static Vector2 focusOver = new Vector2(8f, 8f);

        // ----- the splash (0.92.0, Rob) -----
        // The first screen of the pick: the mod's picture, "Welcome to Controller Support Plus", the line under it, four cards
        // in two rows (Controller Hotbar, Inventory, Controller layout, HUD layouts), each a small looping animation, and
        // one button, A Begin Setup. No step count: the two choices after it are Step 1 of 2 (hotbar) and Step 2 of 2
        // (layout). The animations use the game's own item pictures and button glyphs; the HUD card uses the onboarding's
        // own drawn style (the layout miniatures). Each animation is a function of the time since the splash showed, run
        // every frame from Tick while the splash is up.
        const float SplashTop = -176f, SplashGapY = 28f, ButtonH = 46f, ButtonGap = 26f, ButtonBottom = 26f;
        static readonly List<Action<float>> splashAnims = new List<Action<float>>();
        static float splashStart;
        static GameObject logoGo, beginBtn, noteBand;
        static Vector2 titleHome; static bool titleHomeSet;
        static float SplashH { get { return -SplashTop + 2f * CardH + SplashGapY + ButtonGap + ButtonH + ButtonBottom; } }
        static float PickH { get { return -CardsTop + CardH + NoteGap + NoteH + NoteBottom; } }
        // The splash's flow (0.92.14, Rob; 0.92.19: Begin Setup is a Y shortcut, never walked to). One card plays at a time,
        // left to right, wearing the bag's focus frame, with a yellow bar along its picture filling as it plays; when it has
        // played once the next one starts, and the last one stays on its end. Cards not played yet stand darkened on their
        // opening picture and light up as they play (and stay lit). A moves on to the next card, left and right walk the
        // cards (a card played again from its start). The Begin Setup button, with Y's glyph in it, shows from the last card
        // on, and Y then begins.
        static int splashCur; static float splashCardStart; static bool beginShown;
        static readonly bool[] splashSeen = new bool[4];
        static readonly List<RectTransform> pillars = new List<RectTransform>();
        static readonly List<GameObject> veils = new List<GameObject>();
        static readonly List<Image> progress = new List<Image>();
        /* each card ends as its last action ends (0.92.8, Rob: no still moment at the end) */
        static float[] SplashDur { get { return new float[] { 4.8f, 3.9f, HudSecs * 0.9f, MoreTotal }; } }
        static int splashRefillAt = -1; static bool splashRefilled;
        public static void SplashTick()
        {
            /* 0.92.15 (Rob: the first time the splash opened its text came out huge, in another font, and right the next time):
               the first time it shows, it is drawn once more two frames later, when the text styles have what they measure */
            if (splashRefillAt > 0 && Time.frameCount >= splashRefillAt && Open && step == 0) { splashRefillAt = -1; splashRefilled = true; Fill(); return; }
            if (!Open || step != 0 || splashAnims.Count == 0) return;
            float[] dur = SplashDur;
            float cd = dur[Mathf.Min(splashCur, dur.Length - 1)];
            if (splashCur < 3 && Time.unscaledTime - splashCardStart >= cd) { SplashGo(splashCur + 1); cd = dur[Mathf.Min(splashCur, dur.Length - 1)]; }
            float t = Mathf.Min(Time.unscaledTime - splashCardStart, cd);   /* the last card stays on its end */
            for (int i = 0; i < splashAnims.Count; i++)
            {
                try { splashAnims[i](i == splashCur ? t : 0.4f); }
                catch (Exception e) { Plugin.Log.LogWarning("HUD pick: splash animation " + i + ": " + e.Message); splashAnims[i] = delegate(float u) { }; }
                /* the playing card's progress bar fills as it plays; the others' stand empty */
                if (i < progress.Count && progress[i] != null) progress[i].fillAmount = i == splashCur ? Mathf.Clamp01(t / dur[Mathf.Min(i, dur.Length - 1)]) : 0f;
            }
        }
        /* the card that plays now, from its start, with the focus frame on it */
        static void SplashGo(int i)
        {
            splashCur = Mathf.Clamp(i, 0, 3); splashCardStart = Time.unscaledTime;
            splashSeen[splashCur] = true;
            if (splashCur == 3) beginShown = true;   /* the button shows once the last card is reached (0.92.16, Rob) */
            for (int k = 0; k < veils.Count; k++) if (veils[k] != null) veils[k].SetActive(!splashSeen[k] && k != splashCur);
            if (beginBtn != null) beginBtn.SetActive(beginShown);
            RectTransform on = splashCur < pillars.Count ? pillars[splashCur] : null;
            if (focusFx != null)
            {
                if (on != null)
                {
                    RectTransform frt = (RectTransform)focusFx.transform;
                    frt.SetParent(on, false);
                    frt.anchorMin = frt.anchorMax = frt.pivot = new Vector2(0.5f, 0.5f);
                    frt.anchoredPosition = Vector2.zero; frt.sizeDelta = on.rect.size + focusOver;
                    frt.SetAsLastSibling();
                }
                focusFx.SetActive(on != null);
            }
        }
        static void BuildSplashExtras(InventoryGui gui)
        {
            Vector2 top = new Vector2(0.5f, 1f), bottom = new Vector2(0.5f, 0f);
            Texture2D ic = Icon();
            logoGo = Hints.New("logo", panel);
            RawImage ri = logoGo.AddComponent<RawImage>(); ri.texture = ic; ri.raycastTarget = false; ri.enabled = ic != null;
            Hints.Place(logoGo, top, top, top, new Vector2(0f, -22f), new Vector2(76f, 76f));
            // A Begin Setup: the game's own dialog button (the Quit dialog's), as the Settings window's rows and the pause menu's
            Menu m = Menu.instance; Button src = null;
            if (m != null) foreach (Transform d in new Transform[] { m.m_quitDialog, m.m_logoutDialog }) { if (d == null || src != null) continue; Button[] all = d.GetComponentsInChildren<Button>(true); if (all.Length > 0) src = all[0]; }
            if (src == null && m != null) src = m.m_continueButton;
            if (src != null)
            {
                beginBtn = UnityEngine.Object.Instantiate(src.gameObject, panel, false);
                beginBtn.name = "cp_beginSetup"; beginBtn.SetActive(true);
                foreach (Component c in beginBtn.GetComponentsInChildren<Component>(true)) { if (c == null) continue; string n = c.GetType().Name; if (n == "UIGamePad" || n == "UIInputHint" || n == "Localize") UnityEngine.Object.Destroy(c); }
                Button b = beginBtn.GetComponent<Button>(); b.onClick = new Button.ButtonClickedEvent();
                Navigation nav = b.navigation; nav.mode = Navigation.Mode.None; b.navigation = nav;
                TMP_Text label = null;
                foreach (TMP_Text x in beginBtn.GetComponentsInChildren<TMP_Text>(true)) { if (label == null && x.gameObject.activeSelf) label = x; else x.gameObject.SetActive(false); }
                if (label != null) { label.enableAutoSizing = false; label.enableWordWrapping = false; label.alignment = TextAlignmentOptions.Center; label.text = HintGlyph("JoyButtonY") + "  Begin Setup";   /* Y begins (0.92.19, Rob: a shortcut, the button is never walked to) */ RectTransform lt = label.rectTransform; lt.anchorMin = Vector2.zero; lt.anchorMax = Vector2.one; lt.offsetMin = Vector2.zero; lt.offsetMax = Vector2.zero; }
                RectTransform br = (RectTransform)beginBtn.transform;
                br.anchorMin = br.anchorMax = br.pivot = bottom; br.sizeDelta = new Vector2(240f, ButtonH); br.anchoredPosition = new Vector2(0f, ButtonBottom);
            }
            else Plugin.Log.LogWarning("HUD pick: no game button to copy for Begin Setup");
        }
        // what shows on the splash and what on the two choices. The splash is full screen (0.92.1, Rob): no window, the whole
        // screen under 50% black, its parts floating on it; the two choices keep the game's window.
        static readonly List<GameObject> windowArt = new List<GameObject>(), windowDim = new List<GameObject>();
        static GameObject splashDim, splashBlur;
        // The game's own background blur (0.92.2, Rob): the layer the game's dialogs blur the screen behind them with, found
        // by its material's shader (a 'blur' one) in the split dialog first, then in the pause menu; copied full screen under
        // the splash's black. Every candidate is written to the log; none found, the splash keeps its black alone.
        static GameObject GameBlur(InventoryGui gui, Transform into)
        {
            try
            {
                List<Transform> places = new List<Transform>();
                if (gui != null && gui.m_splitDialog != null) places.Add(gui.m_splitDialog.transform);
                Menu m = Menu.instance; if (m != null) places.Add(m.transform);
                string seen = ""; Graphic pick = null;
                foreach (Transform p in places)
                    foreach (Graphic g in p.GetComponentsInChildren<Graphic>(true))
                    {
                        Material mat = g.material;
                        string sh = mat != null && mat.shader != null ? mat.shader.name : "", n = g.name;
                        if (sh.IndexOf("blur", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("blur", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        seen += " | " + n + " (" + g.GetType().Name + ", shader " + sh + ", " + g.color + ")";
                        if (pick == null) pick = g;
                    }
                Plugin.Log.LogInfo("HUD pick: blur candidates:" + (seen == "" ? " none" : seen));
                if (pick == null) return null;
                GameObject copy = UnityEngine.Object.Instantiate(pick.gameObject, into, false);
                copy.name = "cp_splashBlur";
                for (int i = copy.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(copy.transform.GetChild(i).gameObject);
                foreach (Graphic g in copy.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                RectTransform rt = (RectTransform)copy.transform;
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f); rt.anchoredPosition = Vector2.zero; rt.sizeDelta = Vector2.zero; rt.localScale = Vector3.one;
                Plugin.Log.LogInfo("HUD pick: splash blur copied from '" + pick.name + "'");
                return copy;
            }
            catch (Exception e) { Plugin.Log.LogWarning("HUD pick: blur: " + e.Message); return null; }
        }
        static void SplashParts(bool splash)
        {
            if (logoGo != null) logoGo.SetActive(splash);
            if (beginBtn != null) beginBtn.SetActive(splash);
            if (noteBand != null) noteBand.SetActive(!splash);
            foreach (GameObject g in windowArt) if (g != null) g.SetActive(!splash);
            foreach (GameObject g in windowDim) if (g != null) g.SetActive(false);   /* no dim on the two choices either (0.92.21, Rob: the world behind stays in view while choosing) */
            if (splashDim != null) splashDim.SetActive(splash);
            if (cardWindow != null) cardWindow.SetActive(splash);
            if (splashBlur != null) splashBlur.SetActive(splash);
            /* a window drawn without the game's art has its plain ground on itself, as the dim may be on the root */
            Image ownBg = panel != null ? panel.GetComponent<Image>() : null; if (ownBg != null) ownBg.enabled = !splash;
            Image ownDim = root != null ? root.GetComponent<Image>() : null; if (ownDim != null) ownDim.enabled = false;
            if (title != null)
            {
                RectTransform tr = title.rectTransform;
                if (!titleHomeSet) { titleHomeSet = true; titleHome = tr.anchoredPosition; }
                if (!splash) tr.anchoredPosition = titleHome;
            }
            if (!splash)
            {
                if (stepText != null) ((RectTransform)stepText.transform).anchoredPosition = new Vector2(0f, -50f);
                if (cards != null) { cards.anchoredPosition = new Vector2(0f, CardsTop); cards.localScale = Vector3.one; }
            }
        }
        // the splash's parts, top to bottom: the picture, the title, the line under it; the cards; the button. The same gap
        // between those three groups. 0.92.5 (Rob): the four cards in one row, inside the game's own window (its wood and
        // frame), its padding the same as the gap between the cards; the other parts float outside it. Each card's picture is
        // square, its button labels clear of its bottom edge.
        const float LogoSize = 64f, SplashGap = 34f, SplashMargin = 36f, PillarGap = 14f, BoxPad = 28f, SplashPic = 256f, LabelY = -(256f * 0.5f) + 26f;   /* the window's padding: twice the cards' gap (0.92.7, Rob) */
        const float PillarW = 280f, PillarH = SplashPic + 12f + 26f + 70f;   /* the picture runs to the card's edges (0.92.14, Rob); the text keeps its 12 of padding */
        static GameObject cardWindow;
        static void FillSplash(TMP_Text like)
        {
            splashAnims.Clear(); pillars.Clear(); veils.Clear(); progress.Clear(); beginShown = false; for (int s = 0; s < splashSeen.Length; s++) splashSeen[s] = false;
            splashStart = Time.unscaledTime;
            SplashParts(true);
            RectTransform rr = (RectTransform)root.transform;
            float screenW = rr.rect.width > 10f ? rr.rect.width : 1920f, screenH = rr.rect.height > 10f ? rr.rect.height : 1080f;
            float head = LogoSize + 10f + 34f + 6f + 24f;   /* the picture, the title, the line */
            float cardsH = screenH - 2f * SplashMargin - head - 2f * SplashGap - ButtonH - 2f * BoxPad;
            float k = Mathf.Min(1.15f, cardsH / PillarH, (screenW - 2f * SplashMargin - 3f * PillarGap - 2f * BoxPad) / (4f * PillarW));   /* a little larger than the cards' own size (0.92.7, Rob: 1.6 was too big) */
            k = Mathf.Max(0.7f, k);
            float cw = PillarW * k, ch = PillarH * k, w = 4f * cw + 3f * PillarGap, boxW = w + 2f * BoxPad, boxH = ch + 2f * BoxPad;
            float total = head + SplashGap + boxH + SplashGap + ButtonH;
            panel.sizeDelta = new Vector2(Mathf.Max(boxW, 600f), total);
            panel.position = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, panel.position.z);
            Vector2 top = new Vector2(0.5f, 1f), tl = new Vector2(0f, 1f);
            if (logoGo != null) Hints.Place(logoGo, top, top, top, Vector2.zero, new Vector2(LogoSize, LogoSize));
            if (title != null) { title.rectTransform.anchoredPosition = new Vector2(titleHome.x, -(LogoSize + 10f)); title.text = "Welcome to Controller Support Plus"; }
            ((RectTransform)stepText.transform).anchoredPosition = new Vector2(0f, -(LogoSize + 10f + 34f + 6f));
            stepText.text = "The ideal controller experience in one single package.";
            cards.anchoredPosition = new Vector2(0f, -(head + SplashGap + BoxPad)); cards.localScale = Vector3.one;
            if (beginBtn != null) ((RectTransform)beginBtn.transform).anchoredPosition = Vector2.zero;
            // the window round the cards: the game's own window art (as the pick's window copies it), behind them
            if (cardWindow != null) UnityEngine.Object.Destroy(cardWindow);
            cardWindow = Hints.New("cardWindow", panel);
            Hints.Place(cardWindow, top, top, top, new Vector2(0f, -(head + SplashGap)), new Vector2(boxW, boxH));
            cardWindow.transform.SetSiblingIndex(cards.GetSiblingIndex());
            WindowArt(cardWindow.transform);
            string[] names = { "Controller Hotbar", "Inventory", "HUD layouts", "More improvements" };   /* 0.92.6 (Rob): the extras together in the last card */
            string[] lines = { "Eight items on the D-pad and LT with the face buttons, one press away.", "A controller cursor, and better controls to manage your items.", "Three layouts made for the controller.", "A classic action RPG button layout, and smaller touches you can switch on or off." };
            for (int i = 0; i < 4; i++)
            {
                // the card at its full size (its black ground and the Settings screen's frame), its content drawn at the usual
                // size and scaled up by k, so the pictures grow while the texts keep their size and get more room
                GameObject c = Hints.New("pillar" + i, cards);
                pillars.Add((RectTransform)c.transform);
                Hints.Place(c, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), tl, new Vector2(-w * 0.5f + i * (cw + PillarGap), 0f), new Vector2(cw, ch));
                Image bg = Hints.Img(Hints.New("bg", c.transform), null, new Color(0f, 0f, 0f, 0.2f));   /* 20% black (0.92.12, Rob; 55% before) */
                Hints.Place(bg.gameObject, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
                CardFrame(c.transform);
                GameObject content = Hints.New("content", c.transform);
                RectTransform crt = Hints.Place(content, tl, tl, tl, Vector2.zero, new Vector2(PillarW, PillarH));
                crt.localScale = new Vector3(k, k, 1f);
                GameObject sg = Hints.New("screen", content.transform);
                Hints.Img(sg, null, new Color(0f, 0f, 0f, 0.5f));
                Hints.Place(sg, tl, tl, tl, Vector2.zero, new Vector2(PillarW, SplashPic));   /* edge to edge across the top of the card (0.92.14, Rob) */
                sg.AddComponent<RectMask2D>();
                /* the playing card's progress: a thin bar in the Settings yellow along the picture's bottom, filling as it plays (0.92.14, Rob) */
                Image prog = Hints.Img(Hints.New("progress", sg.transform), Hints.Solid(), ModSettings.Yellow());   /* a filled bar needs art to fill (0.92.15: with none it never showed) */
                Hints.Place(prog.gameObject, Vector2.zero, new Vector2(1f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 3f));
                prog.type = Image.Type.Filled; prog.fillMethod = Image.FillMethod.Horizontal; prog.fillOrigin = 0; prog.fillAmount = 0f;
                progress.Add(prog);
                if (i == 0) AnimHotbar(sg.transform, like);
                else if (i == 1) AnimInventory(sg.transform, like);
                else if (i == 2) AnimHud(sg.transform, sg.transform, like);
                else AnimMore(sg.transform, like);
                prog.transform.SetAsLastSibling();   /* over the picture's content */
                TMP_Text nm = Hints.Text(Hints.New("name", content.transform), like, 20f, Color.white, TextAlignmentOptions.TopLeft);
                Hints.Place(nm.gameObject, tl, tl, tl, new Vector2(12f, -(SplashPic + 12f)), new Vector2(WireW, 28f));
                TextStyle.Apply(nm, TextStyle.Kind.Header);
                nm.text = names[i];
                TMP_Text ln = Hints.Text(Hints.New("line", content.transform), like, 16f, Color.white, TextAlignmentOptions.TopLeft);
                Hints.Place(ln.gameObject, tl, tl, tl, new Vector2(12f, -(SplashPic + 12f + 26f)), new Vector2(WireW, 70f));
                TextStyle.Apply(ln, TextStyle.Kind.Body);
                ln.enableWordWrapping = true;
                ln.text = lines[i];
                /* not played yet: darkened until it plays (0.92.14, Rob) */
                GameObject veil = Hints.New("veil", c.transform); Hints.Img(veil, null, new Color(0f, 0f, 0f, 0.55f));
                Hints.Place(veil, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero); veils.Add(veil);
            }
            Plugin.Log.LogInfo("HUD pick: splash on a " + (int)screenW + " x " + (int)screenH + " screen, cards x" + k.ToString("0.00") + " (" + (int)cw + " x " + (int)ch + ")");
            SplashGo(0); SplashTick();   /* the first card plays first */
            if (!splashRefilled) splashRefillAt = Time.frameCount + 2;   /* the first time: drawn again two frames on (below) */
        }
        /* copies of the game's window art (every layer the pick's window copied: its wood and its frames), filling t */
        static bool artLogged;
        static void WindowArt(Transform t)
        {
            foreach (GameObject g in windowArt)
            {
                if (g == null || (title != null && g == title.gameObject)) continue;
                /* 0.92.9 (Rob): the whole window as every other window draws it, its wood, its darker tone and its framed edges,
                   each layer kept where the window keeps it (its anchors and its overhang copied as they are, not stretched flat) */
                GameObject w = UnityEngine.Object.Instantiate(g, t, false);
                w.SetActive(true);
                RectTransform src = (RectTransform)g.transform, rt = (RectTransform)w.transform;
                rt.anchorMin = src.anchorMin; rt.anchorMax = src.anchorMax; rt.pivot = src.pivot; rt.anchoredPosition = src.anchoredPosition; rt.sizeDelta = src.sizeDelta; rt.localScale = src.localScale;
                if (!artLogged) Plugin.Log.LogInfo("HUD pick: splash window layer '" + g.name + "' anchors " + src.anchorMin + "-" + src.anchorMax + " size " + src.sizeDelta);
                foreach (Graphic gr in w.GetComponentsInChildren<Graphic>(true)) gr.raycastTarget = false;
            }
            artLogged = true;
        }

        // ----- small helpers for the animations -----
        static float Loop(float t, float secs) { return (t % secs) / secs * 100f; }   /* where in its loop, 0-100 */
        static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        /* a value that is 0 before a, rises to 1 by b, stays to c and falls back to 0 by d (percent of the loop) */
        static float Window(float p, float a, float b, float c, float d) { return p < a || p > d ? 0f : p < b ? Ease((p - a) / Mathf.Max(0.001f, b - a)) : p <= c ? 1f : 1f - Ease((p - c) / Mathf.Max(0.001f, d - c)); }
        // keyframes: rows of { percent, x, y, scale, alpha }, eased between neighbours (two rows close together make a jump)
        static Vector4 Keys(float[,] k, float p)
        {
            int n = k.GetLength(0);
            if (p <= k[0, 0]) return new Vector4(k[0, 1], k[0, 2], k[0, 3], k[0, 4]);
            for (int i = 0; i < n - 1; i++)
            {
                if (p > k[i + 1, 0]) continue;
                float u = Ease((p - k[i, 0]) / Mathf.Max(0.0001f, k[i + 1, 0] - k[i, 0]));
                return new Vector4(Mathf.Lerp(k[i, 1], k[i + 1, 1], u), Mathf.Lerp(k[i, 2], k[i + 1, 2], u), Mathf.Lerp(k[i, 3], k[i + 1, 3], u), Mathf.Lerp(k[i, 4], k[i + 1, 4], u));
            }
            return new Vector4(k[n - 1, 1], k[n - 1, 2], k[n - 1, 3], k[n - 1, 4]);
        }
        static TMP_Text AnimText(Transform parent, TMP_Text like, string text, Vector2 at, float size, bool hint)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f);
            TMP_Text g = Hints.Text(Hints.New("text", parent), like, size, Color.white, TextAlignmentOptions.Center);
            Hints.Place(g.gameObject, mid, mid, mid, at, new Vector2(240f, size + 14f));
            if (hint) TextStyle.Apply(g, TextStyle.Kind.Hint);
            g.text = text;
            return g;
        }
        static void Alpha(Graphic g, float a) { if (g == null) return; Color c = g.color; if (Mathf.Abs(c.a - a) > 0.002f) { c.a = a; g.color = c; } }
        static Sprite ring;
        /* a white ring (a circle's edge, 3 of 32 thick), for the cursor and the filling rings */
        static Sprite Ring()
        {
            if (ring != null) return ring;
            const int S = 64; Texture2D tx = new Texture2D(S, S, TextureFormat.RGBA32, false); tx.wrapMode = TextureWrapMode.Clamp;
            Color32[] px = new Color32[S * S];
            for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x - 31.5f) * (x - 31.5f) + (y - 31.5f) * (y - 31.5f));
                    float a = Mathf.Clamp01(31.5f - d) * Mathf.Clamp01(d - 25.5f);
                    px[y * S + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            tx.SetPixels32(px); tx.Apply(false, true);
            ring = Sprite.Create(tx, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
            return ring;
        }
        static readonly Color EquipYellow = new Color(1f, 0.82f, 0.26f, 0.55f), EquipBlue = new Color(96f / 255f, 168f / 255f, 229f / 255f, 0.76f);
        class DemoSlot { public RectTransform rt; public Image fill, flash, bg, border; }
        /* a hotbar slot as the hotbar draws it (black at 50%, its faint edge), with a game item picture, a fill and a flash */
        static DemoSlot MakeSlot(Transform parent, Vector2 at, float size, string item)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f);
            DemoSlot d = new DemoSlot();
            GameObject sq = Hints.New("slot", parent);
            d.rt = Hints.Place(sq, mid, mid, mid, at, new Vector2(size, size));
            d.bg = Hints.Img(sq, Hints.Plate(), new Color(0f, 0f, 0f, 0.5f));
            d.fill = Hints.Img(Hints.New("fill", sq.transform), Hints.Plate(), EquipYellow);
            Hints.Place(d.fill.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
            d.fill.type = Image.Type.Filled; d.fill.fillMethod = Image.FillMethod.Vertical; d.fill.fillOrigin = (int)Image.OriginVertical.Bottom; d.fill.fillAmount = 0f;
            Sprite icon = ItemIcon(item);
            if (icon != null)
            {
                Image im = Hints.Img(Hints.New("icon", sq.transform), icon, Color.white); im.preserveAspect = true;
                Hints.Place(im.gameObject, mid, mid, mid, Vector2.zero, new Vector2(size * 0.85f, size * 0.85f));
            }
            d.flash = Hints.Img(Hints.New("flash", sq.transform), Hints.Plate(), new Color(1f, 1f, 1f, 0f));
            Hints.Place(d.flash.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
            d.border = Hints.Img(Hints.New("border", sq.transform), Hints.Box(), new Color(1f, 1f, 1f, 0.28f)); d.border.type = Image.Type.Sliced;
            Hints.Place(d.border.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
            return d;
        }
        static Sprite ItemIcon(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            try { return drop != null ? drop.m_itemData.GetIcon() : null; } catch (Exception) { return null; }
        }
        /* an equip as the hotbar shows it: the yellow wipe from a to b, a white flash at b, then blue until c (percent) */
        static void EquipAt(DemoSlot s, float p, float a, float b, float c)
        {
            if (p < a || p >= c) { s.fill.fillAmount = 0f; }
            else if (p < b) { s.fill.color = EquipYellow; s.fill.fillAmount = (p - a) / (b - a); }
            else { s.fill.color = EquipBlue; s.fill.fillAmount = 1f; }
            Alpha(s.flash, p >= b && p < b + 8f ? 0.8f * (1f - (p - b) / 8f) : 0f);
        }

        // 1. the Controller Hotbar, as it behaves in play (8 s): D-pad up equips the mace (yellow wipe, white flash, blue); LT
        // held lights the second set and its glyphs; X equips the axe the same way and the mace's blue goes. The cross layout
        // in its own units (54 slots, 64 apart, the sets 208 apart), scaled into the picture.
        static void AnimHotbar(Transform screen, TMP_Text like)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f);
            GameObject rig = Hints.New("hotbar", screen);
            RectTransform rrt = Hints.Place(rig, mid, mid, mid, new Vector2(0f, 18f), new Vector2(400f, 190f));
            float k = 0.58f; rrt.localScale = new Vector3(k, k, 1f);   /* larger in the square picture (0.92.5) */
            string[] items = { "MaceBronze", "MeadHealthMinor", "ShieldWood", "Torch", "Bow", "CookedMeat", "AxeBronze", "Hammer" };
            Vector2[] off = { new Vector2(0f, 64f), new Vector2(0f, -64f), new Vector2(-64f, 0f), new Vector2(64f, 0f) };
            DemoSlot[] s = new DemoSlot[8];
            for (int i = 0; i < 8; i++) { s[i] = MakeSlot(rig.transform, new Vector2(i < 4 ? -104f : 104f, 0f) + off[i % 4], 54f, items[i]); s[i].border.enabled = false; }   /* no white outline (0.92.8, Rob) */
            TMP_Text dpad = AnimText(rig.transform, like, Hints.DpadGlyph(), new Vector2(-104f, 0f), 24f, false);
            string[] face = { "JoyButtonY", "JoyButtonA", "JoyButtonX", "JoyButtonB" };
            Vector2[] fat = { new Vector2(104f, 15.5f), new Vector2(104f, -15.5f), new Vector2(88.5f, 0f), new Vector2(119.5f, 0f) };
            TMP_Text[] fg = new TMP_Text[4];
            for (int i = 0; i < 4; i++) fg[i] = AnimText(rig.transform, like, Hints.Glyph(face[i]), fat[i], 14f, false);
            TMP_Text lt = AnimText(rig.transform, like, Hints.Glyph("JoyLTrigger"), new Vector2(0f, -64f), 20f, false);
            TMP_Text l1 = AnimText(screen, like, Hints.DpadGlyph() + "  Quick slots", new Vector2(0f, LabelY), 16f, true);
            TMP_Text l2 = AnimText(screen, like, "Hold " + Hints.Glyph("JoyLTrigger") + "  Quick slots", new Vector2(0f, LabelY), 16f, true);
            splashAnims.Add(delegate(float t)
            {
                // 0.92.7 (Rob): a seamless loop, 6 s: D-pad up equips the mace (the axe, still in hand from the loop before, goes), then
                // LT + X equips the axe (the mace goes), and it starts again with the axe in hand
                float p = Loop(t, 6f);
                EquipAt(s[0], p, 8f, 22f, 64f);   /* the mace: equipped from D-pad up, until the axe takes its hand */
                EquipAt(s[6], p, 50f, 64f, 101f); if (p < 22f) { s[6].fill.color = EquipBlue; s[6].fill.fillAmount = 1f; }   /* the axe: in hand from the loop before until the mace takes over, then equipped again with LT + X */
                float held = Window(p, 38f, 42f, 72f, 76f);
                for (int i = 4; i < 8; i++) { s[i].bg.color = Color.Lerp(new Color(0f, 0f, 0f, 0.5f), new Color(1f, 1f, 1f, 0.1f), held); Alpha(s[i].border, 0.28f + 0.42f * held); }
                for (int i = 0; i < 4; i++) Alpha(fg[i], 0.3f + 0.7f * held);
                float ls = 1f - 0.12f * held; lt.transform.localScale = new Vector3(ls, ls, 1f);
                float ds = 1f - 0.25f * Window(p, 5f, 7f, 7f, 10f); dpad.transform.localScale = new Vector3(ds, ds, 1f);
                Alpha(l1, Mathf.Max(Window(p, 2f, 5f, 34f, 38f), Window(p, 80f, 84f, 100f, 100f))); Alpha(l2, Window(p, 38f, 42f, 72f, 76f));
            });
        }

        // 2. the bag (12 s), drawn with the bag's own slots and the mod's own controller cursor (0.92.1, Rob: as in the game).
        // A full bag; the left stick: the cursor carries the first wood from one corner of the bag to the other, the slot under
        // it lit with the bag's own focus look as it passes (and the cursor's own hover look: the ring tight with its dot);
        // hold LS: its hold ring fills, then the bag is sorted at once; RT: the mushroom is dropped at once. Then it fades back.
        const float BagSlot = 30f, BagPitch = 34f;
        static Vector2 BagAt(int c, int r) { return new Vector2(-2.5f * BagPitch + c * BagPitch, 14f + BagPitch - r * BagPitch); }
        /* a slot as the bag draws it: the bag's own slot (its element), empty, with its focus frame kept for the hover */
        static GameObject BagSlotOf(Transform parent, Vector2 at)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f);
            InventoryGui gui = InventoryGui.instance;
            GameObject prefab = gui != null && gui.m_playerGrid != null ? gui.m_playerGrid.m_elementPrefab : null;
            if (prefab == null) { DemoSlot d = MakeSlot(parent, at, BagSlot, null); return d.rt.gameObject; }
            GameObject e = UnityEngine.Object.Instantiate(prefab, parent, false);
            e.name = "bagSlot"; e.SetActive(true);
            foreach (Graphic g in e.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            foreach (MonoBehaviour mb in e.GetComponentsInChildren<MonoBehaviour>(true)) if (mb is InventoryElement || mb is Selectable || mb.GetType().Name == "UITooltip") mb.enabled = false;
            InventoryElement el = e.GetComponent<InventoryElement>();
            // only the slot's own background stays: every part the element names (its item, count, durability, marks, focus)
            // is switched off; the focus frame comes back for the hover
            if (el != null)
                foreach (FieldInfo fi in typeof(InventoryElement).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object v = fi.GetValue(el);
                    GameObject part = v is GameObject ? (GameObject)v : v is Component ? ((Component)v).gameObject : null;
                    if (part != null && part != e && part.transform.IsChildOf(e.transform)) part.SetActive(false);
                }
            /* no slot numbers or other text, and the slot black (0.92.3, Rob: numbers showed in the corners, and the light slots lacked contrast) */
            foreach (TMP_Text x in e.GetComponentsInChildren<TMP_Text>(true)) x.gameObject.SetActive(false);
            GameObject focusPart = el != null ? el.m_selected : null;
            foreach (Image im in e.GetComponentsInChildren<Image>(true)) if (focusPart == null || !im.transform.IsChildOf(focusPart.transform)) im.color = new Color(0f, 0f, 0f, 0.5f);
            RectTransform rt = (RectTransform)e.transform;
            float size = rt.rect.width > 1f ? rt.rect.width : 64f;
            rt.anchorMin = rt.anchorMax = rt.pivot = mid; rt.anchoredPosition = at;
            rt.localScale = new Vector3(BagSlot / size, BagSlot / size, 1f);
            return e;
        }
        static void AnimInventory(Transform screen, TMP_Text like)
        {
            List<GameObject> hoverFrames = new List<GameObject>(); List<Vector2> slotAt = new List<Vector2>();
            for (int r = 0; r < 3; r++) for (int c = 0; c < 6; c++)
                {
                    GameObject s = BagSlotOf(screen, BagAt(c, r));
                    InventoryElement el = s.GetComponent<InventoryElement>();
                    hoverFrames.Add(el != null ? el.m_selected : null); slotAt.Add(BagAt(c, r));
                }
            // name, start, moved (or start), sorted, dropped
            string[] names = { "Wood", "Stone", "Resin", "RawMeat", "Wood", "Raspberry", "Mushroom", "AxeBronze", "Torch" };
            int[,] at = { { 0, 0, 5, 2, 2, 0 }, { 2, 0, 2, 0, 4, 0 }, { 5, 0, 5, 0, 5, 0 }, { 1, 1, 1, 1, 0, 1 }, { 3, 1, 3, 1, 3, 0 }, { 0, 2, 0, 2, 1, 1 }, { 4, 2, 4, 2, 2, 1 }, { 2, 2, 2, 2, 0, 0 }, { 5, 1, 5, 1, 1, 0 } };
            Vector2 mid = new Vector2(0.5f, 0.5f);
            List<RectTransform> its = new List<RectTransform>(); List<float[,]> keys = new List<float[,]>(); List<Image> imgs = new List<Image>();
            for (int i = 0; i < names.Length; i++)
            {
                Sprite icon = ItemIcon(names[i]);
                Image im = Hints.Img(Hints.New("item", screen), icon, Color.white); im.preserveAspect = true; im.enabled = icon != null;
                RectTransform rt = Hints.Place(im.gameObject, mid, mid, mid, Vector2.zero, new Vector2(BagSlot * 0.85f, BagSlot * 0.85f));
                Vector2 a = BagAt(at[i, 0], at[i, 1]), m = BagAt(at[i, 2], at[i, 3]), so = BagAt(at[i, 4], at[i, 5]);
                bool moved = i == 0;   /* 0.92.7 (Rob): no drop; quicker move and sort, 6 s */
                List<float[]> k = new List<float[]>();
                k.Add(new float[] { 0f, a.x, a.y, 1f, 1f }); k.Add(new float[] { 10f, a.x, a.y, 1f, 1f });
                if (moved) { k.Add(new float[] { 12f, a.x, a.y, 1.12f, 1f }); k.Add(new float[] { 28f, m.x, m.y, 1.12f, 1f }); k.Add(new float[] { 30f, m.x, m.y, 1f, 1f }); }
                k.Add(new float[] { 52f, m.x, m.y, 1f, 1f }); k.Add(new float[] { 52.3f, so.x, so.y, 1f, 1f });   /* the sort: at once */
                k.Add(new float[] { 85f, so.x, so.y, 1f, 1f }); k.Add(new float[] { 90f, so.x, so.y, 1f, 0f });

                k.Add(new float[] { 91f, a.x, a.y, 1f, 0f }); k.Add(new float[] { 95f, a.x, a.y, 1f, 0f }); k.Add(new float[] { 100f, a.x, a.y, 1f, 1f });
                float[,] kk = new float[k.Count, 5]; for (int r = 0; r < k.Count; r++) for (int q = 0; q < 5; q++) kk[r, q] = k[r][q];
                its.Add(rt); keys.Add(kk); imgs.Add(im);
            }
            // the mod's controller cursor: a dark shade ring, the white ring and its dot (Cursor.Draw's own parts and sizes),
            // drawn at the bag's scale here (the bag's 64 slot is 30)
            float cs = BagSlot / 64f * 1.6f;
            GameObject cur = Hints.New("cursor", screen);
            RectTransform cRt = Hints.Place(cur, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
            cRt.localScale = new Vector3(cs, cs, 1f);
            Image shade = Hints.Img(Hints.New("shade", cur.transform), Hints.Ring(), new Color(0f, 0f, 0f, 0.7f)); Hints.Place(shade.gameObject, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
            Image cRing = Hints.Img(Hints.New("ring", cur.transform), Hints.Ring(), Color.white); Hints.Place(cRing.gameObject, mid, mid, mid, Vector2.zero, new Vector2(26f, 26f));
            Image cDot = Hints.Img(Hints.New("dot", cur.transform), Hints.Disc(), Color.white); Hints.Place(cDot.gameObject, mid, mid, mid, Vector2.zero, new Vector2(5f, 5f));
            CanvasGroup cGroup = cur.AddComponent<CanvasGroup>(); cGroup.blocksRaycasts = false;
            Vector2 rest = BagAt(1, 0), w0 = BagAt(0, 0), w1 = BagAt(5, 2);
            float[,] ck = { { 0, rest.x, rest.y, 1, 1 }, { 6, rest.x, rest.y, 1, 1 }, { 10, w0.x, w0.y, 1, 1 }, { 11, w0.x, w0.y, 0.8f, 1 }, { 12, w0.x, w0.y, 1, 1 }, { 28, w1.x, w1.y, 1, 1 }, { 29, w1.x, w1.y, 0.8f, 1 }, { 30, w1.x, w1.y, 1, 1 }, { 85, w1.x, w1.y, 1, 1 }, { 90, w1.x, w1.y, 1, 0 }, { 95, rest.x, rest.y, 1, 0 }, { 100, rest.x, rest.y, 1, 1 } };
            // the step and its button under the bag; held LS has its hold ring round the glyph
            TMP_Text sa = AnimText(screen, like, Hints.Glyph("JoyLStick") + "  Controller cursor", new Vector2(0f, LabelY), 16f, true);
            GameObject sortRow = Hints.New("sortStep", screen);
            Hints.Place(sortRow, mid, mid, mid, new Vector2(0f, LabelY), new Vector2(200f, 30f));
            CanvasGroup sortGroup = sortRow.AddComponent<CanvasGroup>();
            TMP_Text lsGlyph = AnimText(sortRow.transform, like, Hints.Glyph("JoyLStick"), new Vector2(-40f, 0f), 16f, true);
            Image lsFill = HoldRing(sortRow.transform, new Vector2(-40f, 0f), 26f);
            TMP_Text lsWords = AnimText(sortRow.transform, like, "Hold · Sort", new Vector2(18f, 0f), 16f, true);

            splashAnims.Add(delegate(float t)
            {
                float p = Loop(t, 6f);
                for (int i = 0; i < its.Count; i++)
                {
                    Vector4 v = Keys(keys[i], p);
                    its[i].anchoredPosition = new Vector2(v.x, v.y); its[i].localScale = new Vector3(v.z, v.z, 1f); Alpha(imgs[i], v.w);
                }
                Vector4 cv = Keys(ck, p);
                cRt.anchoredPosition = new Vector2(cv.x, cv.y); cGroup.alpha = cv.w;
                // the slot under the cursor lights with the bag's focus frame; on a slot the cursor's ring is tight and its dot shows
                int over = -1; float best = BagPitch * 0.5f;
                for (int i = 0; i < slotAt.Count; i++) { float d = Vector2.Distance(slotAt[i], new Vector2(cv.x, cv.y)); if (d < best) { best = d; over = i; } }
                for (int i = 0; i < hoverFrames.Count; i++) if (hoverFrames[i] != null && hoverFrames[i].activeSelf != (i == over && cv.w > 0.5f)) hoverFrames[i].SetActive(i == over && cv.w > 0.5f);
                float onSlot = over >= 0 ? 1f - Mathf.Clamp01((best - 3f) / 8f) : 0f;
                float g = (1f + 0.25f * (1f - onSlot)) * cv.z;
                cRing.transform.localScale = new Vector3(g, g, 1f); shade.transform.localScale = new Vector3(g, g, 1f);
                cDot.transform.localScale = new Vector3(onSlot, onSlot, 1f);
                Alpha(sa, Window(p, 2f, 5f, 30f, 34f)); sortGroup.alpha = Window(p, 34f, 37f, 86f, 90f);
                lsFill.fillAmount = Mathf.Clamp01((p - 38f) / 13f);
            });
        }
        /* the mod's hold ring round a glyph (HoldMark's parts): a dark shade, a faint track and the white clockwise fill */
        static Image HoldRing(Transform parent, Vector2 at, float size)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f);
            Image shade = Hints.Img(Hints.New("holdShade", parent), Hints.Ring(), new Color(0f, 0f, 0f, 0.7f)); Hints.Place(shade.gameObject, mid, mid, mid, at, new Vector2(size + 4f, size + 4f));
            Image track = Hints.Img(Hints.New("holdTrack", parent), Hints.Ring(), new Color(1f, 1f, 1f, 0.25f)); Hints.Place(track.gameObject, mid, mid, mid, at, new Vector2(size, size));
            Image fill = Hints.Img(Hints.New("holdFill", parent), Hints.Ring(), Color.white); Hints.Place(fill.gameObject, mid, mid, mid, at, new Vector2(size, size));
            fill.type = Image.Type.Filled; fill.fillMethod = Image.FillMethod.Radial360; fill.fillOrigin = (int)Image.Origin360.Top; fill.fillClockwise = true; fill.fillAmount = 0f;
            return fill;
        }

        // 4. the other improvements (0.92.6, Rob: the last card, the extras blended in): short scenes, each fading into the next.
        // 0.92.7 (Rob): quicker; the buffs and debuffs go through the three styles (Vanilla, Slim, Compact); the equip scene
        // shows the radial ring only (the bar is the game's own); the pickup feed behaves as in play: flush to the right edge,
        // the newest row coming in at the bottom in the highlight yellow, the older ones pushed up and plain.
        static readonly float[] MoreSecs = { 2.2f, 1.8f, 3.5f, 2.5f, 3.5f };   /* layout, buffs, feed, ring, D-pad (0.92.19, Rob: the controls and the buffs shorter still) */
        static float MoreTotal { get { float s = 0f; foreach (float x in MoreSecs) s += x; return s; } }
        static void AnimMore(Transform screen, TMP_Text like)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f);
            List<CanvasGroup> scenes = new List<CanvasGroup>(); List<Action<float>> runs = new List<Action<float>>();
            Func<CanvasGroup> scene = delegate
            {
                GameObject s = Hints.New("scene", screen);
                Hints.Place(s, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                CanvasGroup g = s.AddComponent<CanvasGroup>(); g.alpha = 0f; g.blocksRaycasts = false;
                scenes.Add(g); return g;
            };

            // the PRO controller layout in one picture (0.92.14, Rob): Dodge, Sneak and the Forsaken power stacked, each button
            // beside its name, all three pressed together (0.92.16). 0.92.17 (Rob: the hold ring still sat off the B): each B is
            // drawn exactly as the button guide draws a glyph with its ring, a 34 box with the glyph nudged (-0.1, 1.05) and the
            // ring 32.7 across in the box's middle (the guide's own measured placement)
            {
                Transform s = scene().transform;
                Vector2 l = new Vector2(0f, 0.5f);
                float[] rowY = { 56f, 16f, -24f };
                string[] word = { "Dodge", "Hold · Sneak", "Forsaken power" };
                const float Left = -96f, Box = 34f;
                RectTransform[] press = new RectTransform[3];
                Image fillR = null; Image trackR = null;
                for (int r = 0; r < 2; r++)
                {
                    GameObject box = Hints.New("glyph", s);
                    press[r] = Hints.Place(box, mid, mid, mid, new Vector2(Left + Box * 0.5f, rowY[r]), new Vector2(Box, Box));
                    if (r == 1)
                    {
                        trackR = Hints.Img(Hints.New("track", box.transform), Hints.Ring(), new Color(1f, 1f, 1f, 0.15f));
                        Hints.Place(trackR.gameObject, mid, mid, mid, Vector2.zero, new Vector2(32.7f, 32.7f));
                        fillR = Hints.Img(Hints.New("fill", box.transform), Hints.Ring(), Color.white);
                        Hints.Place(fillR.gameObject, mid, mid, mid, Vector2.zero, new Vector2(32.7f, 32.7f));
                        fillR.type = Image.Type.Filled; fillR.fillMethod = Image.FillMethod.Radial360; fillR.fillOrigin = (int)Image.Origin360.Top; fillR.fillClockwise = true; fillR.fillAmount = 0f;
                    }
                    TMP_Text key = Hints.Text(Hints.New("key", box.transform), like, Plugin.GlyphSize.Value, Color.white, TextAlignmentOptions.Center);
                    Hints.Place(key.gameObject, mid, mid, mid, new Vector2(-0.1f, 1.05f), new Vector2(Box, Box));
                    key.text = Hints.Glyph("JoyButtonB");
                }
                // LB + RB, on the same left edge
                string bumpers = Hints.Glyph("JoyLBumper") + " + " + Hints.Glyph("JoyRBumper");
                TMP_Text bt = Hints.Text(Hints.New("bumpers", s), like, Plugin.GlyphSize.Value, Color.white, TextAlignmentOptions.Center);
                float bw = bt.GetPreferredValues(bumpers).x;
                press[2] = Hints.Place(bt.gameObject, mid, mid, mid, new Vector2(Left + bw * 0.5f, rowY[2] + 1.05f), new Vector2(bw + 4f, Box));
                bt.text = bumpers;
                float names = Left + Mathf.Max(Box, bw) + 12f;
                for (int r = 0; r < 3; r++)
                {
                    TMP_Text w = Hints.Text(Hints.New("word", s), like, 16f, Color.white, TextAlignmentOptions.MidlineLeft);
                    Hints.Place(w.gameObject, mid, mid, l, new Vector2(names, rowY[r]), new Vector2(160f, 30f));
                    TextStyle.Apply(w, TextStyle.Kind.Hint); w.text = word[r];
                }
                runs.Add(delegate(float q)
                {
                    // all three at once: the taps press and let go, the held B's ring fills meanwhile
                    float tap = 1f - 0.15f * Window(q, 20f, 24f, 24f, 32f);
                    float held = 1f - 0.1f * Window(q, 20f, 23f, 70f, 74f);
                    press[0].localScale = new Vector3(tap, tap, 1f);
                    press[2].localScale = new Vector3(tap, tap, 1f);
                    press[1].localScale = new Vector3(held, held, 1f);
                    float ringOn = Window(q, 20f, 22f, 74f, 80f);
                    Alpha(trackR, 0.15f * ringOn); Alpha(fillR, ringOn);
                    fillR.fillAmount = Mathf.Clamp01((q - 22f) / 46f);
                });
            }
            // the buffs and debuffs in the mod's two styles together (0.92.16, Rob): a row of Slim (each in a dark box like the
            // food slots) over a row of Compact (the mod's green or red frame and triangle), each row named beside it. 0.92.17
            // (Rob): room between the names and the boxes, and no pop as they come in
            {
                Transform s = scene().transform;
                Vector4 sz = Buffs.DemoSizes();
                string[] fx = { "Rested", "Shelter", "Wet" }; bool[] bad = { false, false, true };
                string[] names = { "Slim", "Compact" };
                float[] rowY = { 46f, -10f };
                const float Scale = 1.1f, NameRight = -44f, NameGap = 16f;
                float cellW = sz.x * Scale, first = NameRight + NameGap + cellW * 0.5f;
                for (int v = 0; v < 2; v++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        GameObject cell = Hints.New("buff", s);
                        RectTransform crt = Hints.Place(cell, mid, mid, mid, new Vector2(first + i * (cellW + 10f), rowY[v]), new Vector2(sz.x, sz.x));
                        crt.localScale = new Vector3(Scale, Scale, 1f);
                        if (v == 0) Hints.Img(cell, Hints.Plate(), new Color(0f, 0f, 0f, 0.5f));
                        else Hints.Img(cell, null, new Color(0f, 0f, 0f, 0.5f));
                        Sprite icon = StatusIcon(fx[i]);
                        if (icon != null) { Image im = Hints.Img(Hints.New("icon", cell.transform), icon, Color.white); im.preserveAspect = true; Hints.Place(im.gameObject, mid, mid, mid, Vector2.zero, new Vector2(sz.y, sz.y)); }
                        if (v == 1)
                        {
                            Color col = Buffs.DemoColor(bad[i] ? 1 : 0);
                            Image fr = Hints.Img(Hints.New("frame", cell.transform), Buffs.DemoFrame(), col); fr.type = Image.Type.Sliced;
                            Hints.Place(fr.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                            Image mk = Hints.Img(Hints.New("mark", cell.transform), Buffs.DemoTriangle(bad[i]), col);
                            Vector2 a = new Vector2(0.5f, bad[i] ? 1f : 0f);
                            Hints.Place(mk.gameObject, a, a, a, Vector2.zero, new Vector2(sz.z, sz.w));
                        }
                    }
                    TMP_Text n = Hints.Text(Hints.New("style", s), like, 16f, Color.white, TextAlignmentOptions.MidlineRight);
                    Hints.Place(n.gameObject, mid, mid, new Vector2(1f, 0.5f), new Vector2(NameRight, rowY[v]), new Vector2(90f, 30f));
                    TextStyle.Apply(n, TextStyle.Kind.Hint); n.text = names[v];
                }
                AnimText(s, like, "Buffs and debuffs", new Vector2(0f, LabelY), 16f, true);
                runs.Add(delegate(float q) { });   /* still: no pop */
            }
            // the pickup feed, as in play: rows flush to the right edge, on the feed's dark band fading out to the left; the
            // newest comes in at the bottom (sliding in a little from the right) in the highlight yellow, the older ones move up
            // and turn plain
            {
                Transform s = scene().transform;
                string[] items = { "Wood", "Stone", "Resin" };
                float[] arrive = { 8f, 34f, 60f };
                const float RowH = 28f, RowGap = 4f, BottomY = -30f;
                Color hi = new Color(1f, 0.8f, 0.3f), plain = Color.white;
                List<RectTransform> rows = new List<RectTransform>(); List<CanvasGroup> fades = new List<CanvasGroup>(); List<TMP_Text> texts = new List<TMP_Text>();
                Vector2 right = new Vector2(1f, 0.5f);
                for (int i = 0; i < items.Length; i++)
                {
                    GameObject row = Hints.New("row", s);
                    RectTransform rt = Hints.Place(row, right, right, right, new Vector2(0f, BottomY), new Vector2(220f, RowH));
                    Image band = Hints.Img(Hints.New("band", row.transform), FadeBand(), new Color(0f, 0f, 0f, 0.8f));
                    Hints.Place(band.gameObject, right, right, mid, Vector2.zero, new Vector2(440f, RowH));   /* its right half off the edge: solid at the edge, fading to the left */
                    Sprite icon = ItemIcon(items[i]);
                    if (icon != null) { Image im = Hints.Img(Hints.New("icon", row.transform), icon, Color.white); im.preserveAspect = true; Hints.Place(im.gameObject, right, right, right, new Vector2(-6f, 0f), new Vector2(22f, 22f)); }
                    TMP_Text tx = Hints.Text(Hints.New("name", row.transform), like, 15f, plain, TextAlignmentOptions.MidlineRight);
                    Hints.Place(tx.gameObject, right, right, right, new Vector2(-34f, 0f), new Vector2(150f, RowH));
                    tx.text = items[i];
                    CanvasGroup g = row.AddComponent<CanvasGroup>(); g.alpha = 0f;
                    rows.Add(rt); fades.Add(g); texts.Add(tx);
                }
                TMP_Text cap = AnimText(s, like, "Pickup feed", new Vector2(0f, LabelY), 16f, true);
                runs.Add(delegate(float q)
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        float inK = Ease((q - arrive[i]) / 5f);
                        float level = 0f;
                        for (int j = i + 1; j < rows.Count; j++) level += Ease((q - arrive[j]) / 5f);   /* pushed up by each newer row */
                        bool newest = q >= arrive[i] && (i == rows.Count - 1 || q < arrive[i + 1]);
                        fades[i].alpha = inK;
                        rows[i].anchoredPosition = new Vector2(40f * (1f - inK), BottomY + level * (RowH + RowGap));
                        texts[i].color = newest ? hi : plain;
                    }
                    Alpha(cap, Window(q, 4f, 8f, 100f, 100f));
                });
            }
            // equipping: the mod's radial ring round the crosshair (the game's bar is the game's own)
            {
                Transform s = scene().transform;
                Image dot = Hints.Img(Hints.New("crosshair", s), Hints.Disc(), Color.white); Hints.Place(dot.gameObject, mid, mid, mid, new Vector2(0f, 16f), new Vector2(6f, 6f));
                Image ring = HoldRing(s, new Vector2(0f, 16f), 44f);
                TMP_Text cap = AnimText(s, like, "Radial equip ring", new Vector2(0f, LabelY), 16f, true);
                runs.Add(delegate(float q)
                {
                    ring.fillAmount = Mathf.Clamp01((q - 12f) / 60f);
                    Alpha(cap, Window(q, 4f, 8f, 100f, 100f));
                });
            }
            // the D-pad (0.92.16, Rob): its four slots with items; then LT is held and the same slots show the game's actions
            // on LT + D-pad with their own pictures (the radial's consumables and emotes, the hammer, sit), as the hotbar does
            {
                Transform s = scene().transform;
                Vector2[] off = { new Vector2(0f, 64f), new Vector2(0f, -64f), new Vector2(-64f, 0f), new Vector2(64f, 0f) };
                string[] items = { "MaceBronze", "MeadHealthMinor", "ShieldWood", "Torch" };
                GameObject rig = Hints.New("dpad", s);
                RectTransform rrt = Hints.Place(rig, mid, mid, mid, new Vector2(16f, 22f), new Vector2(200f, 200f)); rrt.localScale = new Vector3(0.5f, 0.5f, 1f);
                List<CanvasGroup> itemFade = new List<CanvasGroup>(); List<Image> actions = new List<Image>(); List<DemoSlot> slots = new List<DemoSlot>();
                for (int i = 0; i < 4; i++)
                {
                    DemoSlot d = MakeSlot(rig.transform, off[i], 54f, items[i]);
                    d.border.enabled = false;
                    Transform icon = d.rt.Find("icon");
                    itemFade.Add(icon != null ? icon.gameObject.AddComponent<CanvasGroup>() : null);
                    Sprite act = Favorites.DemoActionIcon(i);
                    Image ai = Hints.Img(Hints.New("action", d.rt), act, new Color(1f, 1f, 1f, 0f)); ai.preserveAspect = true; ai.enabled = act != null;
                    Hints.Place(ai.gameObject, mid, mid, mid, Vector2.zero, new Vector2(40f, 40f));
                    actions.Add(ai); slots.Add(d);
                }
                AnimText(rig.transform, like, Hints.DpadGlyph(), Vector2.zero, 24f, false);
                TMP_Text lt = AnimText(s, like, Hints.Glyph("JoyLTrigger"), new Vector2(-72f, 22f), 26f, false);   /* LT, held, beside the D-pad */
                TMP_Text c1 = AnimText(s, like, Hints.DpadGlyph() + "  Items", new Vector2(0f, LabelY), 16f, true);
                TMP_Text c2 = AnimText(s, like, "Hold " + Hints.Glyph("JoyLTrigger") + " + " + Hints.DpadGlyph() + "  Actions", new Vector2(0f, LabelY), 16f, true);
                runs.Add(delegate(float q)
                {
                    float held = Window(q, 44f, 50f, 100f, 100f);
                    for (int i = 0; i < 4; i++)
                    {
                        if (itemFade[i] != null) itemFade[i].alpha = 1f - held;
                        Alpha(actions[i], held);
                        slots[i].bg.color = Color.Lerp(new Color(0f, 0f, 0f, 0.5f), new Color(1f, 1f, 1f, 0.1f), held);   /* the held set's light, as in play */
                    }
                    Alpha(lt, 0.3f + 0.7f * held); float ls = 1f - 0.12f * held; lt.transform.localScale = new Vector3(ls, ls, 1f);
                    Alpha(c1, 1f - held); Alpha(c2, held);
                });
            }
            splashAnims.Add(delegate(float t)
            {
                float all = t % MoreTotal, at = 0f;
                int now = 0;
                while (now < MoreSecs.Length - 1 && all >= at + MoreSecs[now]) { at += MoreSecs[now]; now++; }
                float q = (all - at) / MoreSecs[now] * 100f;
                for (int i = 0; i < scenes.Count; i++)
                {
                    float fadeP = 0.25f / MoreSecs[i] * 100f;   /* a quarter of a second in and out */
                    float a = i == now ? Mathf.Min(1f, Mathf.Min(q / fadeP, (100f - q) / fadeP)) : 0f;
                    scenes[i].alpha = Mathf.Clamp01(a);
                    if (i == now) runs[i](q);
                }
            });
        }
        /* a status effect's own picture, by the start of its name (Rested, Shelter, Wet...) */
        static Sprite StatusIcon(string name)
        {
            ObjectDB db = ObjectDB.instance;
            if (db == null || db.m_StatusEffects == null) return null;
            foreach (StatusEffect se in db.m_StatusEffects)
                if (se != null && se.m_icon != null && se.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return se.m_icon;
            return null;
        }

        // 4. the HUD layouts (0.92.11, Rob): it starts in Vanilla for a moment, then the pieces glide to their places in Bottom
        // HUD, hold, and glide on to Action RPG. One screen's pieces (health, food, stamina, map, the cross hotbar) drawn in the
        // onboarding's own style with small rounded corners, on the miniature's panel (the bag slots' rounded art, inset from
        // the picture's edges), with padding inside it so nothing reaches its edge. The layout's name at the bottom.
        const float HudSecs = 6f;
        static void AnimHud(Transform card, Transform screen, TMP_Text like)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f), bl = Vector2.zero;
            // the panel: inset 10 from the picture's sides, the 16:9 screen's height
            Image pan = Hints.Img(Hints.New("panel", screen), null, new Color(0f, 0f, 0f, 0.5f)); SlotLook(pan);
            RectTransform prt = Hints.Place(pan.gameObject, mid, mid, mid, new Vector2(0f, 12f), new Vector2(WireW - 20f, WireH));
            // 0.92.17 (Rob): the layouts fade one into the next instead of the pieces gliding: each layout is its own layer of
            // pieces, in the miniature's own 240 x 135 units, scaled to fit the panel less 10 of padding all round
            float k = Mathf.Min((WireW - 20f - 20f) / 240f, (WireH - 20f) / 135f);
            // each piece's place in the three layouts: left, bottom, width, height (Vanilla, Bottom HUD, Action RPG)
            float[][,] at = {
                new float[,] { { 6, 6, 6, 34 }, { 6, 6, 6, 34 }, { 6, 114, 60, 5 } },   /* health: Bottom HUD keeps the game's own bars (0.92.12, Rob) */
                new float[,] { { 15, 6, 7, 7 }, { 15, 6, 7, 7 }, { 6, 122, 7, 7 } }, new float[,] { { 15, 15, 7, 7 }, { 15, 15, 7, 7 }, { 15, 122, 7, 7 } }, new float[,] { { 15, 24, 7, 7 }, { 15, 24, 7, 7 }, { 24, 122, 7, 7 } },
                new float[,] { { 106, 32, 28, 3 }, { 106, 36, 28, 3 }, { 106, 32, 28, 3 } },
                new float[,] { { 204, 99, 30, 30 }, { 204, 6, 30, 30 }, { 204, 6, 30, 30 } }, new float[,] { { 205, 100, 28, 28 }, { 205, 7, 28, 28 }, { 205, 7, 28, 28 } },
                new float[,] { { 182, 6, 52, 23 }, { 94, 6, 52, 23 }, { 6, 6, 52, 23 } } };
            Color[] cols = { Red, Food, Food, Food, Stam, MapEdge, MapFill };
            float[,] cells = { { 1, 2 }, { 0, 1 }, { 2, 1 }, { 1, 0 } };
            CanvasGroup[] lay = new CanvasGroup[3];
            for (int li = 0; li < 3; li++)
            {
                GameObject rig = Hints.New("layout" + li, pan.transform);
                RectTransform rrt = Hints.Place(rig, mid, mid, mid, Vector2.zero, new Vector2(240f, 135f)); rrt.localScale = new Vector3(k, k, 1f);
                lay[li] = rig.AddComponent<CanvasGroup>(); lay[li].alpha = li == 0 ? 1f : 0f;
                for (int pi = 0; pi < cols.Length; pi++)
                {
                    Image im = Hints.Img(Hints.New("piece", rig.transform), Rounded(), cols[pi]); im.type = Image.Type.Sliced;
                    Hints.Place(im.gameObject, bl, bl, bl, new Vector2(at[pi][li, 0], at[pi][li, 1]), new Vector2(at[pi][li, 2], at[pi][li, 3]));
                }
                // the hotbar: two crosses of four 7 slots, 8 apart, the sets 6 apart (52 x 23 in all)
                GameObject hb = Hints.New("hotbar", rig.transform);
                float[,] h = at[cols.Length];
                Hints.Place(hb, bl, bl, bl, new Vector2(h[li, 0], h[li, 1]), new Vector2(52f, 23f));
                for (int s = 0; s < 2; s++) for (int c = 0; c < 4; c++)
                    {
                        Image im = Hints.Img(Hints.New("slot", hb.transform), Rounded(), Slot); im.type = Image.Type.Sliced;
                        Hints.Place(im.gameObject, bl, bl, bl, new Vector2(s * 29f + cells[c, 0] * 8f, cells[c, 1] * 8f), new Vector2(7f, 7f));
                    }
            }
            TMP_Text[] names = new TMP_Text[3];
            for (int i = 0; i < 3; i++) names[i] = AnimText(screen, like, PresetNames[i], new Vector2(0f, LabelY), 16f, true);
            splashAnims.Add(delegate(float t)
            {
                float p = Mathf.Min(100f, t / (HudSecs * 0.9f) * 100f);
                /* a short pause on Vanilla (0-14), fade to Bottom HUD 14-26, hold to 54, fade to Action RPG 54-66 */
                float a1 = p < 14f ? 0f : p < 26f ? Ease((p - 14f) / 12f) : 1f, a2 = p < 54f ? 0f : p < 66f ? Ease((p - 54f) / 12f) : 1f;
                lay[0].alpha = 1f - a1; lay[1].alpha = a1 * (1f - a2); lay[2].alpha = a2;
                Alpha(names[0], 1f - a1); Alpha(names[1], a1 * (1f - a2)); Alpha(names[2], a2);
            });
        }


        // The hotbar step's picture (0.91.2, Rob): the two sets close up, built as the real hotbar draws them, in its own
        // units (a 54 slot, black at 50% with its faint white edge) with sample items and the button glyphs where that
        // layout has them, then scaled to fit the card. Cross: the D-pad glyph in the middle of its set, the face buttons
        // as a small diamond in the other (at 30% until LT is held), LT between the sets level with the bottom slots.
        // Compact cross: each slot's button on its top right corner. Row: each slot's button under it, LT between the groups.
        static readonly string[] SampleItems = { "MaceBronze", "MeadHealthMinor", "AxeBronze", "Torch", "Bow", "CookedMeat", "ShieldWood", "Hammer" };   // up, down, left, right; then Y, A, X, B
        static readonly int[] SampleCounts = { 0, 3, 0, 0, 0, 6, 0, 0 };
        static readonly string[] PadButtons = { "JoyDPadUp", "JoyDPadDown", "JoyDPadLeft", "JoyDPadRight" }, FaceButtons = { "JoyButtonY", "JoyButtonA", "JoyButtonX", "JoyButtonB" };
        const float FaceRestAlpha = 1f;   /* the face glyphs at full strength in the showcase (0.91.6, Rob; 30% as in play before) */
        static void Bar(Transform card, string style, bool focus)
        {
            GameObject sg = Hints.New("screen", card);
            Hints.Img(sg, null, new Color(0f, 0f, 0f, 0.5f));
            Vector2 tl = new Vector2(0f, 1f), mid = new Vector2(0.5f, 0.5f);
            Hints.Place(sg, tl, tl, tl, new Vector2(12f, -12f), new Vector2(WireW, WireH));
            InventoryGui gui = InventoryGui.instance;
            TMP_Text like = gui != null ? (gui.m_weight != null ? gui.m_weight : gui.m_containerName) : null;
            bool row = style == "Row", compact = style == "CompactCross";
            float rowHalf = 1.5f * 64f + 27f + 40f;
            float setX = row ? rowHalf : compact ? 132f : 104f;
            float w = 2f * (row ? rowHalf + 96f + 27f : compact ? 132f + 66f + 27f : 104f + 64f + 27f);
            float top = row ? 27f : compact ? 33f + 27f : 64f + 27f, bottom = row ? 27f + 19f + 12f : compact ? 33f + 27f : 64f + 27f;
            float h = top + bottom;
            float k = Mathf.Min((WireW - 16f) / w, (WireH - 16f) / h);
            GameObject rig = Hints.New("hotbar", sg.transform);
            RectTransform rrt = Hints.Place(rig, mid, mid, mid, new Vector2(0f, (bottom - top) * 0.5f * k), new Vector2(w, h));
            rrt.localScale = new Vector3(k, k, 1f);
            CanvasGroup cg = rig.AddComponent<CanvasGroup>(); cg.alpha = focus ? 1f : 0.7f;
            for (int s = 0; s < 2; s++)
            {
                Vector2 c = new Vector2(s == 0 ? -setX : setX, 0f);
                for (int i = 0; i < 4; i++)
                {
                    Vector2 at = row ? new Vector2((i == 0 ? -0.5f : i == 1 ? 0.5f : i == 2 ? -1.5f : 1.5f) * 64f, 0f)
                        : compact ? (i == 0 ? new Vector2(0f, 33f) : i == 1 ? new Vector2(0f, -33f) : i == 2 ? new Vector2(-66f, 0f) : new Vector2(66f, 0f))
                        : (i == 0 ? new Vector2(0f, 64f) : i == 1 ? new Vector2(0f, -64f) : i == 2 ? new Vector2(-64f, 0f) : new Vector2(64f, 0f));
                    SampleSlot(rig.transform, c + at, SampleItems[s * 4 + i], SampleCounts[s * 4 + i], like);
                    string btn = s == 0 ? PadButtons[i] : FaceButtons[i];
                    float a = s == 0 ? 1f : FaceRestAlpha;
                    if (row) GlyphAt(rig.transform, like, btn, c + at + new Vector2(0f, -27f - 19f), 15f, a);
                    else if (compact) GlyphAt(rig.transform, like, btn, c + at + new Vector2(27f - 6f, 27f - 6f), 15f, a);
                    else if (s == 1) GlyphAt(rig.transform, like, btn, c + (i == 0 ? new Vector2(0f, 15.5f) : i == 1 ? new Vector2(0f, -15.5f) : i == 2 ? new Vector2(-15.5f, 0f) : new Vector2(15.5f, 0f)), 14f, a);
                }
                if (s == 0 && !row && !compact)
                {
                    string dp = Hints.DpadGlyph();
                    if (!string.IsNullOrEmpty(dp)) GlyphAt(rig.transform, like, null, c, 24f, 1f, dp);
                }
            }
            GlyphAt(rig.transform, like, "JoyLTrigger", new Vector2(0f, row ? 0f : compact ? -33f : -64f), 20f, 1f);
        }
        static void GlyphAt(Transform parent, TMP_Text like, string button, Vector2 at, float size, float alpha, string text = null)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f);
            TMP_Text g = Hints.Text(Hints.New("glyph", parent), like, size, new Color(1f, 1f, 1f, alpha), TextAlignmentOptions.Center);
            Hints.Place(g.gameObject, mid, mid, mid, at, new Vector2(40f, 40f));
            g.text = text ?? Hints.Glyph(button);
        }
        static void SampleSlot(Transform parent, Vector2 at, string item, int count, TMP_Text like)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f);
            GameObject sq = Hints.New("slot", parent);
            Hints.Place(sq, mid, mid, mid, at, new Vector2(54f, 54f));
            Hints.Img(sq, Hints.Plate(), new Color(0f, 0f, 0f, 0.5f));   /* the slot's box (Favorites' BoxRest) */
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item) : null;
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            Sprite icon = null;
            try { icon = drop != null ? drop.m_itemData.GetIcon() : null; } catch (Exception) { icon = null; }
            if (icon != null)
            {
                Image im = Hints.Img(Hints.New("icon", sq.transform), icon, Color.white);
                im.preserveAspect = true;
                Hints.Place(im.gameObject, mid, mid, mid, Vector2.zero, new Vector2(46f, 46f));
            }
            Image border = Hints.Img(Hints.New("border", sq.transform), Hints.Box(), new Color(1f, 1f, 1f, 0.28f));   /* its faint edge (Favorites' BorderIdle) */
            border.type = Image.Type.Sliced;
            Hints.Place(border.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
            if (count > 1)
            {
                TMP_Text n = Hints.Text(Hints.New("count", sq.transform), like, 13f, Color.white, TextAlignmentOptions.Right);
                Hints.Place(n.gameObject, Vector2.zero, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-4f, 2f), new Vector2(0f, 16f));
                n.text = count.ToString();
            }
        }

        // A layout as empty boxes on a 240 x 135 screen (16:9), drawn 256 x 144 in the card. Places follow the presets:
        // Vanilla keeps the game's health and food bottom left and the map top right, the hotbar bottom right; Bottom HUD
        // the hotbar bottom centre and the map bottom right; Action RPG the health and food top left, the hotbar bottom left.
        static readonly Color Red = new Color(0.75f, 0.22f, 0.17f), Stam = new Color(0.95f, 0.76f, 0.2f), Food = new Color(0.54f, 0.42f, 0.23f), MapFill = new Color(0.17f, 0.17f, 0.17f), MapEdge = new Color(0.35f, 0.35f, 0.35f), Slot = new Color(0.85f, 0.85f, 0.85f);
        static void Wire(Transform card, string preset, string hot, bool focus)
        {
            GameObject sg = Hints.New("screen", card);
            Image screen = Hints.Img(sg, null, new Color(0f, 0f, 0f, 0.5f));
            Vector2 tl = new Vector2(0f, 1f);
            SnapToPixel(Hints.Place(sg, tl, tl, tl, new Vector2(12f, -12f), new Vector2(WireW, WireH)));
            boxIn = sg.transform; boxLit = focus;
            if (preset == "ActionRPG")
            {
                for (int f = 0; f < 3; f++) box(Food, 6f + 9f * f, 122f, 7f, 7f);
                box(Red, 6f, 114f, 60f, 5f);
            }
            else
            {
                box(Red, 6f, 6f, 6f, 34f);
                for (int f = 0; f < 3; f++) box(Food, 15f, 6f + 9f * f, 7f, 7f);
            }
            box(Stam, 106f, preset == "BottomHUD" ? 36f : 32f, 28f, 3f);   /* Bottom HUD: over the hotbar (24 overlapped it, 0.92.1) */
            float my = preset == "Vanilla" ? 99f : 6f;
            box(MapEdge, 204f, my, 30f, 30f); box(MapFill, 205f, my + 1f, 28f, 28f);
            // the hotbar on a pixel grid of its own: one slot size and one step for every slot, so the crosses and rows are
            // even (0.91.6). The step is the slot plus a gap of at least one pixel; the sets 6 units apart.
            bool row = hot == "Row";
            float kpx = WireW / 240f * PixelsPerUnit();
            float cell = Mathf.Max(3f, Mathf.Round(7f * kpx)), pitch = cell + Mathf.Max(1f, Mathf.Round((row ? 2f : 1f) * kpx)), setGap = Mathf.Round(6f * kpx);
            int cols = row ? 4 : 3;
            float setW = (cols - 1) * pitch + cell, wAll = 2f * setW + setGap;
            float edge = Mathf.Round(6f * kpx), screenW = Mathf.Round(WireW * PixelsPerUnit());
            float x0 = preset == "Vanilla" ? screenW - edge - wAll : preset == "BottomHUD" ? Mathf.Round((screenW - wAll) * 0.5f) : edge;
            bool compactBar = hot == "CompactCross";   /* the compact cross: up and down a half step from the sides (0.91.15) */
            float[,] cells = row ? new float[,] { { 0, 0 }, { 1, 0 }, { 2, 0 }, { 3, 0 } } : compactBar ? new float[,] { { 1, 1.1f }, { 0, 0.55f }, { 2, 0.55f }, { 1, 0 } } : new float[,] { { 1, 2 }, { 0, 1 }, { 2, 1 }, { 1, 0 } };
            for (int s = 0; s < 2; s++)
                for (int c = 0; c < 4; c++)
                    boxPx(Slot, x0 + s * (setW + setGap) + cells[c, 0] * pitch, edge + Mathf.Round(cells[c, 1] * pitch), cell, cell);
        }
        // a box by its left and bottom on the 240 x 135 screen, in the screen being drawn
        static Transform boxIn; static bool boxLit;
        // On whole screen pixels (0.91.6, Rob: the miniature hotbar looked misaligned): the box's left and bottom and its
        // size are each rounded to a whole pixel, so equal boxes are drawn equal and every gap comes out the same.
        static void box(Color col, float x, float yb, float w, float h)
        {
            float k = WireW / 240f, s = PixelsPerUnit();
            boxPx(col, Mathf.Round(x * k * s), Mathf.Round(yb * k * s), Mathf.Max(1f, Mathf.Round(w * k * s)), Mathf.Max(1f, Mathf.Round(h * k * s)));
        }
        static void boxPx(Color col, float xPx, float yPx, float wPx, float hPx)
        {
            float s = PixelsPerUnit();
            Image im = Hints.Img(Hints.New("b", boxIn), Rounded(), boxLit ? col : new Color(col.r, col.g, col.b, 0.7f));
            im.type = Image.Type.Sliced;   /* small rounded corners (0.92.8, Rob) */
            Vector2 bl = Vector2.zero;
            Hints.Place(im.gameObject, bl, bl, bl, new Vector2(xPx / s, yPx / s), new Vector2(wPx / s, hPx / s));
        }
        static float PixelsPerUnit() { float s = boxIn != null ? Mathf.Abs(boxIn.lossyScale.x) : 1f; return s > 0.0001f ? s : 1f; }
        /* a white plate with small rounded corners (radius 3), stretchable: for the miniatures' boxes (0.92.8) */
        static Sprite rounded;
        static Sprite Rounded()
        {
            if (rounded != null) return rounded;
            const int S = 8; const float R = 3f;
            Texture2D tx = new Texture2D(S, S, TextureFormat.RGBA32, false); tx.wrapMode = TextureWrapMode.Clamp; tx.filterMode = FilterMode.Bilinear;
            Color32[] px = new Color32[S * S];
            for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
                {
                    float cx = Mathf.Max(0f, Mathf.Max(R - (x + 0.5f), (x + 0.5f) - (S - R))), cy = Mathf.Max(0f, Mathf.Max(R - (y + 0.5f), (y + 0.5f) - (S - R)));
                    float a = Mathf.Clamp01(R + 0.5f - Mathf.Sqrt(cx * cx + cy * cy));
                    px[y * S + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            tx.SetPixels32(px); tx.Apply(false, true);
            rounded = Sprite.Create(tx, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(R, R, R, R));
            return rounded;
        }
        /* the bag slot's own background art (its rounded corners), on an Image of any size, keeping the Image's colour */
        static void SlotLook(Image im)
        {
            InventoryGui gui = InventoryGui.instance;
            GameObject prefab = gui != null && gui.m_playerGrid != null ? gui.m_playerGrid.m_elementPrefab : null;
            Image src = prefab != null ? prefab.GetComponent<Image>() : null;
            if (src == null || src.sprite == null) { im.sprite = Rounded(); im.type = Image.Type.Sliced; return; }
            im.sprite = src.sprite; im.type = src.sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple; im.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier; im.material = src.material;
        }
        // the picture's own corner moved onto a whole screen pixel, so the boxes in it land on pixels too
        static void SnapToPixel(RectTransform rt)
        {
            Vector3[] q = new Vector3[4]; rt.GetWorldCorners(q);
            Vector3 d = new Vector3(Mathf.Round(q[0].x) - q[0].x, Mathf.Round(q[0].y) - q[0].y, 0f);
            if (d.sqrMagnitude > 0.000001f) rt.position += d;
        }
    }

    // ---------- keyboard and mouse: the game's own hotbar where the preset puts the hotbar ----------
    // With ApplyHudPresetToKeyboardAndMouse on, playing on keyboard and mouse in Bottom HUD or Action RPG, the game's own
    // hotbar (1 to 8) moves to where the Controller Hotbar sits in that preset: its bottom on the minimap's bottom line,
    // centred (Bottom HUD, Center), just left of the minimap (Bottom HUD, Right) or at the left edge (Action RPG). Moved by
    // an offset on top of the game's own place just before drawing, and handed back on the controller, in the Vanilla
    // preset, and when the setting is off. With it centred, the game's stamina bars are lifted over it as over the
    // Controller Hotbar.
    static class GameHotbar
    {
        static readonly Vector3[] c = new Vector3[4];
        static bool hooked, moved, centred;
        static RectTransform bar, homeOf;
        // The game's own place for the bar, noted before the mod first moves it, and put back exactly (0.90.3). Moved by an
        // offset until then, the bar stayed in the preset's corner (over the health panel) after switching to keyboard and
        // mouse with the layout not kept there, whenever its position had been touched in between.
        static Vector2 home;
        static void GoHome()
        {
            if (moved && bar != null && homeOf == bar && bar.anchoredPosition != home) { bar.anchoredPosition = home; Plugin.Log.LogInfo("Game hotbar: back to its own place"); }
            moved = false;
        }
        static Rect drawn;
        public static void Tick() { if (!hooked) { hooked = true; Canvas.willRenderCanvases += Apply; } }
        // where it is drawn, when it has been moved to the bottom centre
        public static bool CentredRect(out float x0, out float x1, out float top)
        {
            x0 = drawn.xMin; x1 = drawn.xMax; top = drawn.yMax;
            return moved && centred;
        }
        static void Apply()
        {
            try
            {
                Hud h = Hud.instance;
                if (h == null) return;
                if (bar == null) { HotkeyBar hb = h.GetComponentInChildren<HotkeyBar>(true); bar = hb != null ? hb.transform as RectTransform : null; moved = false; }
                if (bar == null) return;
                if (homeOf != bar) { homeOf = bar; home = bar.anchoredPosition; moved = false; }   /* a new HUD: its own place, untouched yet */
                bool want = Plugin.Enabled.Value && !ZInput.IsGamepadActive() && Plugin.HudOnKbm.Value && Plugin.HudPreset.Value != "Vanilla" && bar.gameObject.activeInHierarchy;
                float right, top;
                if (!want || !MapPlace.PicGaps(out right, out top)) { GoHome(); return; }
                float s = bar.parent != null ? Mathf.Max(0.0001f, bar.parent.lossyScale.x) : 1f;
                if (!moved) home = bar.anchoredPosition;   /* wherever the game has it right now, before the mod moves it */
                Vector2 now = (bar.anchoredPosition - home) * s;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach (Graphic g in bar.GetComponentsInChildren<Graphic>(false))
                {
                    if (!g.enabled || g.color.a < 0.02f) continue;
                    g.rectTransform.GetWorldCorners(c);
                    for (int i = 0; i < 4; i++) { x0 = Mathf.Min(x0, c[i].x - now.x); y0 = Mathf.Min(y0, c[i].y - now.y); x1 = Mathf.Max(x1, c[i].x - now.x); y1 = Mathf.Max(y1, c[i].y - now.y); }
                }
                if (x0 == float.MaxValue) return;
                float w = x1 - x0, gap = 24f * s;
                string at = Plugin.HotbarAt;
                float tx;
                if (at == "Center") tx = Screen.width * 0.5f - w * 0.5f;
                else if (at == "Left") tx = right;
                else
                {
                    tx = Screen.width - right - w;
                    Minimap mm = Minimap.instance;
                    if (Plugin.MinimapBottom && !MapPlace.Hidden && mm != null && mm.m_mapImageSmall != null && mm.m_mapImageSmall.gameObject.activeInHierarchy)
                    { mm.m_mapImageSmall.rectTransform.GetWorldCorners(c); tx = c[0].x - gap - w; }
                }
                Vector2 by = new Vector2(tx - x0, top - y0);   // its bottom on the minimap's bottom line
                Vector2 want2 = home + by / s;
                if (bar.anchoredPosition != want2) bar.anchoredPosition = want2;
                drawn = Rect.MinMaxRect(x0 + by.x, y0 + by.y, x1 + by.x, y1 + by.y);
                moved = true; centred = at == "Center";
            }
            catch (Exception e) { Plugin.Log.LogWarning("Game hotbar: " + e.Message); }
        }
    }

    // ---------- Buffs and debuffs: the slim version ----------
    // The game's status effects (an icon with its name under it, in the top right) drawn as small boxes like the food
    // slots: the game's own icon, its own time or stack count in the bottom right corner, its cooldown shade and its red
    // blink copied every frame. The game's own list keeps running unseen, so nothing about the effects changes. Three places:
    //   Default             where the game has its list (into the corner when the minimap is moved down or hidden)
    //   BesideMinimap       just left of the minimap, held by its bottom; more rows grow upward; lifted over the hotbar
    //   UnderResourceBars   a row of the resource bars group, between the bars and the Forsaken power
    // A place the layout cannot give (no group, no minimap) falls back to Default. Bottom HUD and Action RPG only: the
    // Vanilla preset keeps the game's own.
    static class Buffs
    {
        /* Detailed: 75% of the food slot's 45 (0.92.13), 5 apart (1 before) */
        const float BoxSide = 34f, BoxPitch = 39f, BoxArt = 32f, Gap = 16f, BoxText = 0.75f;
        // Action RPG, TopLeft: floating icons (no box) in a row above the health bar: 30 icons in 36 places, 6 apart
        const float FloatSide = 45f, FloatPitch = 46f, FloatArt = 43f;   // the food slots' size and spacing, without their box
        // Slim: floating icons with no box (any position). Compact: the food slot's box.
        public static bool Floating { get { return Plugin.BuffStyle.Value == "Slim"; } }
        // Action RPG's top left row over the resource bars is one long row, never wrapped
        static bool OneRow { get { return UnderBars && Plugin.PresetActionRpg; } }
        static bool oneRowNow;
        static bool floatNow;
        static float Side { get { return floatNow ? FloatSide : BoxSide; } }
        static float Pitch { get { return floatNow ? FloatPitch : BoxPitch; } }
        static float Art { get { return floatNow ? FloatArt : BoxArt; } }
        const int BoxPerRow = 8;
        // the floating row in the top left corner is one long row, never wrapped
        static int PerRow { get { return oneRowNow ? 1000 : BoxPerRow; } }
        class Cell { public GameObject go; public Image bg, icon, cool, arrow, frame, mark; public TMP_Text time; }
        static readonly List<Cell> cells = new List<Cell>();
        static GameObject root;
        static Sprite bgSprite; static Color bgColor = new Color(0f, 0f, 0f, 0.6f); static Image.Type bgType = Image.Type.Simple;
        static TMP_Text timeLike;
        static bool hiding;
        static FieldInfo listField, effectsField;
        static readonly Vector3[] c = new Vector3[4], d = new Vector3[4];

        public static bool Hiding { get { return hiding; } }
        public static RectTransform Box { get { return root != null && root.activeSelf ? root.transform as RectTransform : null; } }
        static bool On { get { return Plugin.LayoutOn && Plugin.SlimBuffs && Hud.instance != null && Player.m_localPlayer != null; } }
        // asked by the resource bars group, which keeps a row for them
        public static bool UnderBars { get { return On && Plugin.BuffPos.Value == "TopLeft" && Plugin.PresetActionRpg; } }   // only Action RPG has its resource bars in the top left

        static List<RectTransform> Game(Hud h)
        {
            if (listField == null) listField = AccessTools.Field(typeof(Hud), "m_statusEffects");
            return listField != null ? listField.GetValue(h) as List<RectTransform> : null;
        }
        public static int Count()
        {
            Hud h = Hud.instance;
            List<RectTransform> l = h != null ? Game(h) : null;
            int n = 0;
            if (l != null) foreach (RectTransform e in l) if (e != null && e.gameObject.activeSelf) n++;
            return n;
        }
        // the height a block of n boxes takes, in the group's own units
        public static float Height(int n) { if (n <= 0) return 0f; int per = OneRow ? 1000 : BoxPerRow; int rows = (n + per - 1) / per; float p = Floating ? FloatPitch : BoxPitch, sd = Floating ? FloatSide : BoxSide; return rows * p - (p - sd); }

        public static void Off()
        {
            if (root != null && root.activeSelf) root.SetActive(false);
            Release();
        }
        static void Release()
        {
            if (!hiding) return;
            hiding = false;
            Hud h = Hud.instance;
            CanvasGroup g = h != null && h.m_statusEffectListRoot != null ? h.m_statusEffectListRoot.GetComponent<CanvasGroup>() : null;
            if (g != null) g.alpha = 1f;
        }
        static void HideGame(Hud h)
        {
            RectTransform l = h.m_statusEffectListRoot;
            if (l == null) return;
            CanvasGroup g = l.GetComponent<CanvasGroup>();
            if (g == null) { g = l.gameObject.AddComponent<CanvasGroup>(); g.blocksRaycasts = false; }
            if (g.alpha != 0f) g.alpha = 0f;
            hiding = true;
        }

        public static void Tick()
        {
            if (!On) { Off(); return; }
            Hud h = Hud.instance;
            HideGame(h);   // hidden also while the bag is open, so the game's own never pops in for a moment
            bool show = !Hud.IsUserHidden() && !Plugin.BagCovers && !Minimap.IsOpen();
            if (root == null || root.transform.parent == null) Build(h);
            if (root == null) return;
            List<RectTransform> game = Game(h);
            int n = 0;
            if (game != null) foreach (RectTransform e in game) if (e != null && e.gameObject.activeSelf) n++;
            show &= n > 0;
            if (root.activeSelf != show) root.SetActive(show);
            if (!show) return;
            Transform space = root.transform.parent;
            // the game's food slot size on screen: drawn at the scale of the game's own health panel
            if (h.m_healthPanel != null && space != null)
            {
                float k = h.m_healthPanel.lossyScale.x / Mathf.Max(0.0001f, space.lossyScale.x);
                if (k > 0.2f && k < 5f && Mathf.Abs(root.transform.localScale.x - k) > 0.001f) root.transform.localScale = new Vector3(k, k, 1f);
            }
            while (cells.Count < n) cells.Add(MakeCell());

            // where: a corner of the block on a point of the screen, and which way the boxes run from it
            // the setting's names (by where they sit in Action RPG) to the places they mean
            string at = Plugin.BuffPos.Value == "TopLeft" ? "UnderResourceBars" : Plugin.BuffPos.Value == "BottomRight" ? "BesideMinimap" : Plugin.BuffPos.Value.StartsWith("TopRight") ? "Default" : Plugin.BuffPos.Value;
            RectTransform slot = Vitals.BuffSlot;
            Minimap mm = Minimap.instance;
            RectTransform pic = mm != null && mm.m_mapImageSmall != null ? mm.m_mapImageSmall.rectTransform : null;
            // TopLeft: in Action RPG a row of its resource bars group; anywhere else the top left corner of the screen (until
            // 0.69.3 it fell back to the top right there, and in Bottom HUD it sat in the bottom left group)
            if (at == "UnderResourceBars" && (slot == null || !Plugin.PresetActionRpg)) at = "TopLeftCorner";
            if (at == "BesideMinimap" && (pic == null || !pic.gameObject.activeInHierarchy || MapPlace.Hidden)) at = "Default";
            float scale = root.transform.lossyScale.x;
            floatNow = Floating;
            oneRowNow = at == "UnderResourceBars" && Plugin.PresetActionRpg;
            int across = Mathf.Min(n, PerRow), down = (n + PerRow - 1) / PerRow;   // a row block: boxes per row, rows
            bool column = at == "BottomLeft";   // bottom left: one column reading upward, the next column to its right
            float bw = ((column ? down : across) * Pitch - (Pitch - Side)) * scale, bh = ((column ? across : down) * Pitch - (Pitch - Side)) * scale;
            Vector2 pivot; Vector3 point;
            if (at == "UnderResourceBars")
            {
                slot.GetWorldCorners(c);
                pivot = new Vector2(0f, 1f); point = c[1];
            }
            else if (at == "BesideMinimap")
            {
                pic.GetWorldCorners(c);
                pivot = new Vector2(1f, 0f); point = new Vector3(c[0].x - Gap * scale, c[0].y, 0f);
                point.y = Lift(Rect.MinMaxRect(point.x - bw, point.y, point.x, point.y + bh), h, scale);   // the hotbar just left of the map (Bottom HUD, Right)
            }
            else if (at == "TopLeftCorner")
            {
                // the minimap's edge distance from the top and the left, running right, more rows downward
                float edge = Edge(pic, scale);
                pivot = new Vector2(0f, 1f); point = new Vector3(edge, Screen.height - edge, 0f);
            }
            else if (at == "BottomLeft" || at == "BottomCenter")
            {
                // from the bottom edge at the minimap's own edge distance, standing on whatever is already there
                float edge = Edge(pic, scale);
                pivot = column ? new Vector2(0f, 0f) : new Vector2(0.5f, 0f);
                point = column ? new Vector3(edge, edge, 0f) : new Vector3(Screen.width * 0.5f, edge, 0f);
                float x0 = column ? point.x : point.x - bw * 0.5f;
                point.y = Lift(Rect.MinMaxRect(x0, point.y, x0 + bw, point.y + bh), h, scale);
            }
            else
            {
                // the game's own first effect: its top right corner (the game runs its list leftward from there)
                RectTransform first = null;
                foreach (RectTransform e in game) if (e != null && e.gameObject.activeSelf) { first = e; break; }
                Transform icon = first != null ? first.Find("Icon") : null;
                ((RectTransform)(icon != null ? icon : first)).GetWorldCorners(c);
                pivot = new Vector2(1f, 1f); point = c[2];
            }
            RectTransform rr = (RectTransform)root.transform;
            if (rr.pivot != pivot) rr.pivot = pivot;
            rr.position = new Vector3(point.x, point.y, rr.position.z);

            // the boxes, in the game's order: the first nearest the point, running away from it, a new row (or column) after 8
            // the effects the game drew them for, one for one with its list (it walks both by the same index)
            if (effectsField == null) effectsField = AccessTools.Field(typeof(Hud), "m_tempStatusEffects");
            List<StatusEffect> effects = effectsField != null ? effectsField.GetValue(h) as List<StatusEffect> : null;
            int i = 0, gi = -1;
            foreach (RectTransform e in game)
            {
                gi++;
                if (e == null || !e.gameObject.activeSelf) continue;
                Cell cell = cells[i];
                StatusEffect se = effects != null && gi < effects.Count ? effects[gi] : null;
                int col = i % PerRow, row = i / PerRow;
                Vector2 a, pos;
                if (column) { a = Vector2.zero; pos = new Vector2(row * Pitch, col * Pitch); }
                else if (at == "BottomCenter")
                {
                    int inRow = Mathf.Min(PerRow, n - row * PerRow);   // each row centred on its own
                    a = new Vector2(0.5f, 0f); pos = new Vector2((col - (inRow - 1) * 0.5f) * Pitch, row * Pitch);
                }
                else
                {
                    bool leftward = at != "UnderResourceBars" && at != "TopLeftCorner", upward = at == "BesideMinimap";
                    a = new Vector2(leftward ? 1f : 0f, upward ? 0f : 1f);
                    pos = new Vector2(leftward ? -col * Pitch : col * Pitch, upward ? row * Pitch : -row * Pitch);
                }
                RectTransform crt = (RectTransform)cell.go.transform;
                if (crt.anchorMin != a || crt.pivot != a) { crt.anchorMin = crt.anchorMax = crt.pivot = a; }
                crt.anchoredPosition = pos;
                Look(cell, Kind(se));
                if (!cell.go.activeSelf) cell.go.SetActive(true);
                Copy(e, cell, se);
                i++;
            }
            for (int j = i; j < cells.Count; j++) if (cells[j].go.activeSelf) cells[j].go.SetActive(false);
            rr.sizeDelta = new Vector2(bw, bh) / Mathf.Max(0.0001f, scale);
        }

        // The minimap's distance from the right edge of the screen (the HUD's edge distance), in screen pixels.
        static float Edge(RectTransform pic, float scale)
        {
            if (pic != null && pic.gameObject.activeInHierarchy && !MapPlace.Hidden)
            {
                pic.GetWorldCorners(d);
                float g = Screen.width - d[2].x;
                if (g >= 0f && g < Screen.width * 0.2f) return g;
            }
            return 30f * scale;
        }

        // The block's bottom, raised until it is clear of everything already on the screen there: the Controller Hotbar (16
        // apart), and every set of resource bars, set further apart (32, as the Forsaken power is from the bars): the mod's
        // group, the game's own health and food panel (its vertical health bar and the food under it, in the bottom left
        // with Vanilla bars) and the game's stamina and eitr bars.
        const float BarsGap = 32f;
        static float Lift(Rect r, Hud h, float scale)
        {
            List<Rect> under = new List<Rect>();
            List<float> gaps = new List<float>();
            Rect o;
            if (Drawn(Favorites.HudBox, out o)) { under.Add(o); gaps.Add(Gap); }
            if (Drawn(Vitals.Box, out o)) { under.Add(o); gaps.Add(BarsGap); }
            foreach (RectTransform bars in new RectTransform[] { h.m_healthPanel, h.m_foodBarRoot, h.m_staminaBar2Root, h.m_eitrBarRoot })
                if (bars != null && !Vitals.HidesNow(bars.gameObject) && Drawn(bars, out o)) { under.Add(o); gaps.Add(BarsGap); }
            for (int pass = 0; pass < 6; pass++)
            {
                bool moved = false;
                for (int k = 0; k < under.Count; k++)
                {
                    Rect u = under[k];
                    float gap = gaps[k] * scale;
                    // overlapping it, or nearer above it than its gap
                    if (u.xMax > r.xMin && u.xMin < r.xMax && u.yMax + gap > r.yMin && u.yMin < r.yMax) { r.y = u.yMax + gap; moved = true; }
                }
                if (!moved) break;
            }
            return r.yMin;
        }

        // the screen box of what is drawn under t (enabled graphics, faded or not, so a bar that fades out keeps its room)
        static bool Drawn(Transform t, out Rect r)
        {
            r = new Rect();
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (Graphic g in t.GetComponentsInChildren<Graphic>(false))
            {
                if (!g.enabled) continue;
                g.rectTransform.GetWorldCorners(d);
                x0 = Mathf.Min(x0, d[0].x); y0 = Mathf.Min(y0, d[0].y); x1 = Mathf.Max(x1, d[2].x); y1 = Mathf.Max(y1, d[2].y);
            }
            if (x0 == float.MaxValue) return false;
            r = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        // A worded value keeps only its number, so it fits the box: the comfort effect's "Comfort: 5" reads "5". Times
        // ("12:30", "5m") have no words before the colon and stay as they are.
        static string lastIn = "", lastOut = "";
        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s == lastIn) return lastOut;
            string o = s;
            int k = s.LastIndexOf(':');
            if (k > 0 && k < s.Length - 1)
            {
                string head = s.Substring(0, k), tail = s.Substring(k + 1).Trim();
                bool words = false; foreach (char ch in head) if (char.IsLetter(ch)) { words = true; break; }
                bool number = tail.Length > 0; foreach (char ch in tail) if (!char.IsDigit(ch)) { number = false; break; }
                if (words && number) o = tail;
            }
            lastIn = s; lastOut = o;
            return o;
        }

        // one effect, as the game has it this frame: its icon (with its red blink), its time or stacks, its cooldown shade
        static void Copy(RectTransform e, Cell cell, StatusEffect se)
        {
            Transform t = e.Find("Icon");
            Image src = t != null ? t.GetComponent<Image>() : null;
            if (src != null) { if (cell.icon.sprite != src.sprite) cell.icon.sprite = src.sprite; if (cell.icon.color != src.color) cell.icon.color = src.color; }
            cell.icon.enabled = src != null && src.sprite != null;
            t = e.Find("TimeText");
            TMP_Text tt = t != null ? t.GetComponent<TMP_Text>() : null;
            string time = Short(tt != null && tt.gameObject.activeSelf ? tt.text : "");
            // a timed effect counts down as the food does: whole minutes, rounded up ("23m"), and only its last minute in
            // seconds ("45s"), blinking as the food's does then. Untimed values (stacks, comfort) stay as the game writes them.
            Color tc = Color.white;
            if (se != null && se.m_ttl > 0f && time != "")
            {
                float left = se.m_ttl - se.GetDuration();
                if (left >= 60f) time = Mathf.CeilToInt(left / 60f) + "m";
                else { time = Mathf.Max(0, Mathf.FloorToInt(left)) + "s"; tc = new Color(1f, 1f, 1f, 0.4f + Mathf.Sin(Time.time * 10f) * 0.6f); }
            }
            if (cell.time.text != time) cell.time.text = time;
            if (cell.time.color != tc) cell.time.color = tc;
            t = e.Find("Cooldown");
            Image cd = t != null ? t.GetComponent<Image>() : null;
            bool cool = cd != null && t.gameObject.activeSelf && cd.enabled;
            if (cell.cool.enabled != cool) cell.cool.enabled = cool;
            if (cool) { cell.cool.sprite = cd.sprite; cell.cool.color = cd.color; cell.cool.type = cd.type; cell.cool.fillMethod = cd.fillMethod; cell.cool.fillAmount = cd.fillAmount; cell.cool.fillOrigin = cd.fillOrigin; }
        }

        static void Build(Hud h)
        {
            if (h.m_rootObject == null) return;
            foreach (Cell cl in cells) if (cl.go != null) UnityEngine.Object.Destroy(cl.go);
            cells.Clear();
            Vector2 tr = new Vector2(1f, 1f);
            root = Hints.New("ControllerPro_Buffs", h.m_rootObject.transform);
            Hints.Place(root, Vector2.zero, Vector2.zero, tr, Vector2.zero, new Vector2(Side, Side));
            // the food slot's own look: its background and its time text
            Image foodBg = h.m_foodIcons != null && h.m_foodIcons.Length > 0 && h.m_foodIcons[0] != null && h.m_foodIcons[0].transform.parent != null ? h.m_foodIcons[0].transform.parent.GetComponent<Image>() : null;
            if (foodBg != null) { bgSprite = foodBg.sprite; bgColor = foodBg.color; bgType = foodBg.type; }
            else bgSprite = Hints.Plate();
            timeLike = h.m_foodTime != null && h.m_foodTime.Length > 0 && h.m_foodTime[0] != null ? h.m_foodTime[0] : h.m_healthText;
        }

        static Cell MakeCell()
        {
            Vector2 mid = new Vector2(0.5f, 0.5f), br = new Vector2(1f, 0f);
            Cell cell = new Cell();
            cell.go = Hints.New("buff", root.transform);
            Hints.Place(cell.go, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(Side, Side));
            Image bg = Hints.Img(cell.go, bgSprite, bgColor);
            cell.bg = bg;
            bg.type = bgType;
            cell.icon = Hints.Img(Hints.New("icon", cell.go.transform), null, Color.white);
            cell.icon.preserveAspect = true;
            Hints.Place(cell.icon.gameObject, mid, mid, mid, Vector2.zero, new Vector2(Art, Art));
            cell.cool = Hints.Img(Hints.New("cooldown", cell.go.transform), null, Color.white);
            cell.cool.enabled = false;
            Hints.Place(cell.cool.gameObject, mid, mid, mid, Vector2.zero, new Vector2(Art, Art));
            if (timeLike != null)
            {
                cell.time = Hints.Text(Hints.New("time", cell.go.transform), timeLike, timeLike.fontSize, Color.white, TextAlignmentOptions.BottomRight);
                cell.time.fontSharedMaterial = timeLike.fontSharedMaterial; cell.time.fontStyle = timeLike.fontStyle;
            }
            else cell.time = Hints.Text(Hints.New("time", cell.go.transform), null, 14f, Color.white, TextAlignmentOptions.BottomRight);
            Hints.Place(cell.time.gameObject, br, br, br, new Vector2(-2f, 2f), new Vector2(BoxSide - 4f, 25f));
            // a debuff's small down arrow in the top right corner, white with the dark shadow the game's texts have
            Vector2 tr = new Vector2(1f, 1f);
            cell.arrow = Hints.Img(Hints.New("debuff", cell.go.transform), Arrow(), Red());
            cell.arrow.preserveAspect = true;
            Hints.Place(cell.arrow.gameObject, tr, tr, tr, Vector2.zero, new Vector2(11f, 11f));
            Shadow sh = cell.arrow.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.9f); sh.effectDistance = new Vector2(0f, -1f);
            cell.arrow.enabled = false;
            /* Compact (0.92.1): a buff's green frame with a small triangle standing on its bottom edge, a debuff's red frame with
               the triangle hanging down from its top edge */
            cell.frame = Hints.Img(Hints.New("frame", cell.go.transform), Frame(), Color.white);
            cell.frame.type = Image.Type.Sliced; cell.frame.raycastTarget = false;
            Hints.Place(cell.frame.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
            cell.mark = Hints.Img(Hints.New("mark", cell.go.transform), Triangle(false), Color.white);
            cell.mark.raycastTarget = false;
            cell.frame.enabled = cell.mark.enabled = false;
            return cell;
        }

        // one box's look for its place: the food slot's box, or (Action RPG, TopLeft) a floating icon with no box
        /* the compact look's parts for the splash's showcase (0.92.6): its frame, its triangle, its colours and sizes */
        public static Sprite DemoFrame() { return Frame(); }
        public static Sprite DemoTriangle(bool down) { return Triangle(down); }
        public static Color DemoColor(int kind) { return Colour(kind); }
        public static Vector4 DemoSizes() { return new Vector4(BoxSide, BoxArt, MarkW, MarkH); }
        // kind: 0 buff, 1 debuff, 2 neutral (0.92.13)
        static Color Colour(int kind) { return kind == 1 ? Red() : kind == 2 ? Grey : Green; }
        static float timeSize = -1f;
        static void Look(Cell cell, int kind)
        {
            bool debuff = kind == 1;
            RectTransform crt = (RectTransform)cell.go.transform;
            Vector2 sz = new Vector2(Side, Side), art = new Vector2(Art, Art);
            if (crt.sizeDelta != sz) crt.sizeDelta = sz;
            RectTransform irt = cell.icon.rectTransform;
            if (irt.sizeDelta != art) { irt.sizeDelta = art; cell.cool.rectTransform.sizeDelta = art; }
            if (timeSize < 0f) timeSize = cell.time.fontSize;
            float ts = floatNow ? timeSize : timeSize * BoxText;
            if (cell.time.fontSize != ts) { cell.time.fontSize = ts; cell.time.rectTransform.sizeDelta = new Vector2(Side - 4f, 25f); }
            if (cell.bg.enabled == floatNow) cell.bg.enabled = !floatNow;
            if (cell.arrow.enabled != (floatNow && debuff)) cell.arrow.enabled = floatNow && debuff;
            bool boxed = !floatNow;
            if (cell.frame.enabled != boxed) cell.frame.enabled = boxed;
            bool marked = boxed && kind != 2;   // a neutral one: the grey frame alone
            if (cell.mark.enabled != marked) cell.mark.enabled = marked;
            if (!boxed) return;
            Color col = Colour(kind);
            if (cell.frame.color != col) { cell.frame.color = col; cell.mark.color = col; }
            // the triangle, inside the frame: on the bottom edge pointing up (buff), or at the very top pointing down (debuff;
            // 0.92.1 turned the up one over on its top edge, which put it outside the box)
            Vector2 a = new Vector2(0.5f, debuff ? 1f : 0f);
            RectTransform mrt = cell.mark.rectTransform;
            if (mrt.anchorMin != a) { mrt.anchorMin = mrt.anchorMax = mrt.pivot = a; mrt.sizeDelta = new Vector2(MarkW, MarkH); mrt.anchoredPosition = Vector2.zero; mrt.localEulerAngles = Vector3.zero; }
            Sprite want = Triangle(debuff);
            if (cell.mark.sprite != want) cell.mark.sprite = want;
        }
        static readonly Color Green = new Color(0.45f, 0.85f, 0.35f, 1f), Grey = new Color(0.72f, 0.72f, 0.72f, 1f);
        const float MarkW = 8f, MarkH = 5f, FrameLine = 2f;   /* 75% of 10 x 6 with the box (0.92.13) */
        // the passing ones that do nothing much on their own: resting toward Rested, sheltered, warm by a fire
        static readonly string[] neutral = { "resting", "shelter", "fire" };
        static int Kind(StatusEffect se)
        {
            if (se == null) return 0;
            string n = (se.m_name ?? "").ToLowerInvariant();
            foreach (string w in helpful) if (n.Contains(w)) return 0;
            foreach (string w in harmful) if (n.Contains(w)) return 1;
            foreach (string w in neutral) if (n.Contains(w)) return 2;
            return 0;
        }

        // The game has no debuff flag, so Kind goes by the effect's name: one of the known harmful ones. A mead or potion is
        // never one, even when its name holds a harmful word (frost resistance, poison resistance). (0.65.0 also counted
        // every effect the game blinks, and that caught potions.)
        static readonly string[] harmful = { "wet", "cold", "freezing", "frost", "burning", "poison", "lightning", "smoke", "tared", "encumbered", "slimed" };
        static readonly string[] helpful = { "resist", "res_", "mead", "potion", "tonic", "barleywine" };
        // the game's own warning red (the one it blinks an effect's icon with), or a plain red if it cannot be read
        static Color Red()
        {
            FieldInfo fi = AccessTools.Field(typeof(Hud), "s_colorRedish");
            object v = fi != null ? fi.GetValue(null) : null;
            return v is Color ? (Color)v : new Color(1f, 0.25f, 0.2f);
        }

        // a white frame, FrameLine thick, sliced so it stays that thick at any box size
        static Sprite frameSprite;
        static Sprite Frame()
        {
            if (frameSprite != null) return frameSprite;
            const int S = 16; int L = (int)FrameLine;
            Texture2D tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point; tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
                tex.SetPixel(x, y, x < L || y < L || x >= S - L || y >= S - L ? Color.white : new Color(1f, 1f, 1f, 0f));
            tex.Apply();
            frameSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(L + 1, L + 1, L + 1, L + 1));
            return frameSprite;
        }
        // a small solid triangle, smooth edges: pointing up with its base along the bottom, or (down) pointing down with its
        // base along the top
        static Sprite triUp, triDown;
        static Sprite Triangle(bool down)
        {
            if (!down && triUp != null) return triUp;
            if (down && triDown != null) return triDown;
            const int W = 40, H = 24;
            Texture2D tex = new Texture2D(W, H, TextureFormat.RGBA32, true);
            tex.filterMode = FilterMode.Trilinear; tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                float fromBase = down ? H - 1 - y : y;
                float half = (W * 0.5f) * (1f - (fromBase + 0.5f) / H);   // the half width left at this height
                float dd = half - Mathf.Abs(x + 0.5f - W * 0.5f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(dd + 0.5f)));
            }
            tex.Apply();
            Sprite s = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
            if (down) triDown = s; else triUp = s;
            return s;
        }
        // a plain down arrow, drawn once: a shaft and a head, white, for the debuff mark
        static Sprite arrowSprite;
        static Sprite Arrow()
        {
            if (arrowSprite != null) return arrowSprite;
            const int S = 32;
            Texture2D tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear; tex.wrapMode = TextureWrapMode.Clamp;
            Color on = Color.white, off = new Color(1f, 1f, 1f, 0f);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    // y counts up from the bottom: the head is the bottom half, the shaft the top half
                    bool shaft = y >= 14 && y < 30 && Mathf.Abs(x - 15.5f) <= 3f;
                    bool head = y >= 2 && y < 16 && Mathf.Abs(x - 15.5f) <= (y - 2) + 0.5f;
                    tex.SetPixel(x, y, shaft || head ? on : off);
                }
            tex.Apply();
            arrowSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
            return arrowSprite;
        }
    }
#if DEV
    // Once per game session, a few seconds into a world, the layout of the game's own vital displays (health, stamina,
    // eitr, adrenaline, food, Forsaken power) is written to the log. It is what HUD layout presets are built on.
    static class HudDump
    {
        static bool done;
        static float since = -1f;
        static readonly Vector3[] c = new Vector3[4];
        public static void Maybe()
        {
            if (done || Hud.instance == null || Player.m_localPlayer == null) { if (Player.m_localPlayer == null) since = -1f; return; }
            if (since < 0f) { since = Time.unscaledTime; return; }
            if (Time.unscaledTime - since < 5f) return;
            done = true;
            Hud h = Hud.instance;
            System.Text.StringBuilder sb = new System.Text.StringBuilder("HUD layout dump (screen " + Screen.width + "x" + Screen.height + ")\n");
            Dump(sb, "health", h.m_healthBarRoot, 0); Dump(sb, "healthPanel", h.m_healthPanel, 0);
            Dump(sb, "food", h.m_foodBarRoot, 0); Dump(sb, "foodBaseBar", h.m_foodBaseBar, 0);
            Dump(sb, "stamina", h.m_staminaBar2Root, 0); Dump(sb, "eitr", h.m_eitrBarRoot, 0);
            Dump(sb, "adrenaline", h.m_adrenalineBarRoot, 0); Dump(sb, "forsakenPower", h.m_gpRoot, 0);
            Plugin.Log.LogInfo(sb.ToString());
        }
        static void Dump(System.Text.StringBuilder sb, string label, RectTransform rt, int depth)
        {
            if (rt == null) { sb.Append(label + ": none\n"); return; }
            rt.GetWorldCorners(c);
            sb.Append(new string(' ', depth * 2)).Append(depth == 0 ? label + " = " : "").Append(rt.name)
              .Append(rt.gameObject.activeInHierarchy ? "" : " (hidden)")
              .Append(" amin ").Append(rt.anchorMin).Append(" amax ").Append(rt.anchorMax).Append(" pivot ").Append(rt.pivot)
              .Append(" pos ").Append(rt.anchoredPosition).Append(" size ").Append(rt.sizeDelta).Append(" rect ").Append(rt.rect.size)
              .Append(" screen ").Append(new Vector2(c[0].x, c[0].y)).Append("-").Append(new Vector2(c[2].x, c[2].y));
            Image im = rt.GetComponent<Image>();
            if (im != null) sb.Append(" img ").Append(im.sprite != null ? im.sprite.name : "-").Append(" ").Append(im.type).Append(im.type == Image.Type.Filled ? " " + im.fillMethod + " origin " + im.fillOrigin : "");
            foreach (Component comp in rt.GetComponents<Component>())
            {
                string n = comp.GetType().Name;
                if (n != "RectTransform" && n != "Image" && n != "CanvasRenderer") sb.Append(" [").Append(n).Append("]");
            }
            sb.Append("\n");
            if (depth < 3) foreach (Transform ch in rt) { RectTransform r = ch as RectTransform; if (r != null) Dump(sb, null, r, depth + 1); }
        }
    }
#endif

    static class HoldMark
    {
        public static float Progress;          // set by Core each frame: the hold filling now, 0 when none
        public static InventoryGrid Grid;      // the grid the focus is in
        static GameObject slotRing;
        static Image fill;

        static float lastR, fullUntil;
        static InventoryGrid lastGrid;

        public static void Tick()
        {
            float r = InventoryGui.IsVisible() ? Progress : 0f;
            // A ring that completed stays full for a short beat. The hold fires as the ring completes, and the window it
            // opens (split, assign) only takes over on the next frame; without the beat the plain cursor showed for that
            // one frame in between.
            float now = Time.unscaledTime;
            if (r <= 0f && lastR >= 0.95f) fullUntil = now + 0.15f;
            if (r > 0f) { fullUntil = 0f; lastGrid = Grid; }
            lastR = r;
            if (r <= 0f && now < fullUntil && InventoryGui.IsVisible()) { r = 1f; if (Grid == null) Grid = lastGrid; }
            Cursor.ShowHold(Cursor.On ? r : 0f);
            RectTransform el = !Cursor.On && r > 0f && Grid != null ? Grid.GetGamepadSelectedElement() : null;
            if (el == null) { if (slotRing != null && slotRing.activeSelf) slotRing.SetActive(false); return; }
            if (slotRing == null)
            {
                Vector2 mid = new Vector2(0.5f, 0.5f);
                slotRing = Hints.New("ControllerPro_SlotHold", el);
                RectTransform rt = (RectTransform)slotRing.transform;
                Image shade = Hints.Img(Hints.New("shade", slotRing.transform), Hints.Ring(), new Color(0f, 0f, 0f, 0.7f));
                Hints.Place(shade.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, new Vector2(4f, 4f));
                Image track = Hints.Img(Hints.New("track", slotRing.transform), Hints.Ring(), new Color(1f, 1f, 1f, 0.25f));
                Hints.Place(track.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                fill = Hints.Img(Hints.New("fill", slotRing.transform), Hints.Ring(), Color.white);
                Hints.Place(fill.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                fill.type = Image.Type.Filled; fill.fillMethod = Image.FillMethod.Radial360;
                fill.fillOrigin = (int)Image.Origin360.Top; fill.fillClockwise = true;
            }
            if (slotRing.transform.parent != el) slotRing.transform.SetParent(el, false);
            // centred on the slot, a little over half its size, so the item stays readable around it
            RectTransform r2 = (RectTransform)slotRing.transform;
            r2.anchorMin = r2.anchorMax = r2.pivot = new Vector2(0.5f, 0.5f);
            float s = Mathf.Min(el.rect.width, el.rect.height) * 0.6f;
            r2.sizeDelta = new Vector2(s, s); r2.anchoredPosition = Vector2.zero;
            slotRing.transform.SetAsLastSibling();
            if (!slotRing.activeSelf) slotRing.SetActive(true);
            fill.fillAmount = r;
        }
    }

    // After a transfer, the slots the item went into on the other side flash white: the same flash as a favorite
    // that has just finished equipping. It shows where the item landed (a stack it topped up, or a new slot).
    static class Landed
    {
        class Flash { public InventoryGrid grid; public Vector2i pos; public float start; public Image img; }
        static readonly List<Flash> live = new List<Flash>();
        static readonly FieldInfo F_elements = AccessTools.Field(typeof(InventoryGrid), "m_elements");

        public static Dictionary<Vector2i, int> Snapshot(InventoryGrid g)
        {
            Dictionary<Vector2i, int> d = new Dictionary<Vector2i, int>();
            Inventory inv = g != null ? g.GetInventory() : null;
            if (inv == null) return d;
            foreach (ItemDrop.ItemData it in inv.GetAllItems()) d[it.m_gridPos] = it.m_stack;
            return d;
        }

        public static void Mark(InventoryGrid g, Dictionary<Vector2i, int> before)
        {
            Inventory inv = g != null ? g.GetInventory() : null;
            if (inv == null || before == null) return;
            foreach (ItemDrop.ItemData it in inv.GetAllItems())
            {
                int was;
                if (before.TryGetValue(it.m_gridPos, out was) && was >= it.m_stack) continue;   // untouched
                Flash f = new Flash(); f.grid = g; f.pos = it.m_gridPos; f.start = Time.unscaledTime;
                live.Add(f);
            }
        }

        static RectTransform ElementAt(InventoryGrid g, Vector2i pos)
        {
            List<InventoryElement> els = F_elements.GetValue(g) as List<InventoryElement>;
            if (els == null) return null;
            foreach (InventoryElement el in els) if (el != null && el.Position == pos) return el.transform as RectTransform;
            return null;
        }

        public static void Tick()
        {
            float dur = Mathf.Max(0.05f, Plugin.FavFlash.Value > 0f ? Plugin.FavFlash.Value : 0.3f);
            for (int i = live.Count - 1; i >= 0; i--)
            {
                Flash f = live[i];
                float t = (Time.unscaledTime - f.start) / dur;
                RectTransform el = f.grid != null && InventoryGui.IsVisible() ? ElementAt(f.grid, f.pos) : null;
                if (t >= 1f || el == null)
                {
                    if (f.img != null) UnityEngine.Object.Destroy(f.img.gameObject);
                    live.RemoveAt(i);
                    continue;
                }
                if (f.img == null || f.img.transform.parent != el)
                {
                    if (f.img != null) UnityEngine.Object.Destroy(f.img.gameObject);
                    f.img = Hints.Img(Hints.New("cp_landed", el), Hints.Plate(), Color.clear);
                    RectTransform r = (RectTransform)f.img.transform;
                    r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
                }
                // Behind the item, not over it: first in the slot, so only the slot's own background lights up and the
                // item's picture, count and marks stay on top. A lighter slot, not a white square: it reads as the
                // background brightening, then settling back to the usual slot colour with the favorites' timing.
                f.img.transform.SetAsFirstSibling();
                f.img.color = new Color(1f, 1f, 1f, 0.32f * (1f - t) * (1f - t));
            }
        }
    }

    // ---------- pickup feed ----------
    // On the controller, pickups show as a short list instead of the game's one-at-a-time message in the top left. The
    // newest is at the bottom, highlighted, and older ones sit above; at most 5, fewer when there is no room. The same item
    // again while its row shows adds to its count and comes back to the bottom. A row stays 4 s, then fades. Its look is the
    // game's own: the pickup message's text, the message icon's size, the yellow of the Forsaken power's time. Where it sits
    // (Rob's wireframe, layout only): flush to the right edge, above whatever is in the bottom right and below whatever is
    // in the top right. Setting: Pick Up Notification Style, New (this feed) or Vanilla (the game's message). What is around it is
    // measured as it goes, so it moves with the button guide (taller in build mode) and never overlaps.
    [HarmonyPatch(typeof(Character), "ShowPickupMessage")]
    static class Patch_PickupMessage
    {
        static bool Prefix(Character __instance, ItemDrop.ItemData item, int amount)
        {
            try
            {
                if (item != null && __instance == Player.m_localPlayer && PickupFeed.Active) { PickupFeed.Add(item, amount); return false; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Pickup feed: " + e.Message); }
            return true;   // the game's own message
        }
    }

    // Action RPG: the game's top-left message (its text and icon) moves down just enough to sit below the resource bar
    // group, which takes that corner. The offset is measured each frame from where the group actually ends (Horizontal or
    // Mixed, with or without eitr), and from where the message would be without it. Everywhere else it stays the game's.
    static class MessageNudge
    {
        const float Gap = 12f;
        static readonly Nudge text = new Nudge(), icon = new Nudge();
        static readonly Vector3[] c = new Vector3[4];
        public static void Tick()
        {
            MessageHud mh = MessageHud.instance;
            if (mh == null || mh.m_messageText == null) return;
            RectTransform t = mh.m_messageText.rectTransform, i = mh.m_messageIcon != null ? mh.m_messageIcon.rectTransform : null;
            RectTransform box = Plugin.PresetActionRpg ? Vitals.Box : null;
            float down = 0f;
            if (box != null)
            {
                float scale = Mathf.Max(0.0001f, t.lossyScale.y);
                box.GetWorldCorners(c); float boxBottom = c[0].y;
                // the message's top as the game has it (without our move)
                t.GetWorldCorners(c); float top = c[1].y - text.Present(t).y * (t.parent != null ? t.parent.lossyScale.y : 1f);
                if (i != null) { i.GetWorldCorners(c); top = Mathf.Max(top, c[1].y - icon.Present(i).y * (i.parent != null ? i.parent.lossyScale.y : 1f)); }
                float over = top - (boxBottom - Gap * scale);
                if (over > 0f) down = over;
            }
            text.Set(t, new Vector2(0f, -down / Mathf.Max(0.0001f, t.parent != null ? t.parent.lossyScale.y : 1f)));
            if (i != null) icon.Set(i, new Vector2(0f, -down / Mathf.Max(0.0001f, i.parent != null ? i.parent.lossyScale.y : 1f)));
        }
    }

    // While the feed is in use, the game's "Dropped ..." message in the top left is not shown either. Every other message
    // there ("Inventory full"...) stays.
    [HarmonyPatch(typeof(Player), "Message")]
    static class Patch_DropMessage
    {
        static bool Prefix(Player __instance, MessageHud.MessageType type, string msg)
        {
            try
            {
                if (type == MessageHud.MessageType.TopLeft && msg != null && msg.StartsWith("$msg_dropped", StringComparison.Ordinal)
                    && __instance == Player.m_localPlayer && PickupFeed.Active) return false;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Drop message: " + e.Message); }
            return true;
        }
    }

    static class PickupFeed
    {
        const int Max = 5;
        const float MapClear = 40f;   // Rob: 40 between the feed and the minimap (and its button guide)
        const float Life = 4f, Fade = 0.4f, Gap = 4f, Clear = 12f, IconGap = 6f, Measure = 0.2f;
        // smaller than the game's message: 70% of its icon, 80% of its text; each row on a dark band that fades out on the
        // text's side (both sides when centred), padded 4 round the content, in the game's own dark tone
        const float IconScale = 0.7f, TextScale = 0.8f, Pad = 4f, BandFade = 48f, BandAlpha = 0.8f, SlideIn = 0.2f, SlideDist = 40f;
        public static bool Active { get { return Plugin.LayoutOn && Plugin.FeedPos.Value != "Vanilla" && Hud.instance != null; } }

        class Entry { public string key, name; public Sprite icon; public int n; public float at; }
        class Row { public RectTransform rt; public TMP_Text text; public Image icon, band, flash; public string shown; public float bandW; }
        static readonly List<Entry> entries = new List<Entry>();
        static readonly Row[] rows = new Row[Max];
        static GameObject root;
        static float iconSize = 22f, floorY, ceilY, edgeX, measuredAt = -1f;
        static Color bandColor = new Color(0f, 0f, 0f, 0.55f);
        static Sprite rampOne;
        static Color highlight = new Color(1f, 0.8f, 0.3f), plain = Color.white;
        static readonly Vector3[] c4 = new Vector3[4];

        public static void Add(ItemDrop.ItemData item, int amount)
        {
            string key = item.m_shared.m_name;
            Entry e = entries.Find(x => x.key == key);
            if (e != null) { entries.Remove(e); e.n += amount; }
            else
            {
                e = new Entry(); e.key = key; e.n = amount; e.icon = item.GetIcon();
                e.name = Localization.instance != null ? Localization.instance.Localize(key) : key;
            }
            e.at = Time.unscaledTime;
            entries.Add(e);
            while (entries.Count > Max) entries.RemoveAt(0);
            // no sound of its own: the game already plays its pickup sound
        }

        // a horizontal fade: clear at the left to full at the right (one), or clear at both ends and full in the middle (both)
        static Sprite Ramp(bool both)
        {
            const int w = 64;
            Color32[] px = new Color32[w * 2];
            for (int x = 0; x < w; x++)
            {
                float t = x / (w - 1f), a = both ? 1f - Mathf.Abs(t * 2f - 1f) : t;
                a = a * a * (3f - 2f * a);
                px[x] = px[w + x] = new Color(1f, 1f, 1f, a);
            }
            Texture2D tex = new Texture2D(w, 2, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp; tex.SetPixels32(px); tex.Apply(false, true); tex.hideFlags = HideFlags.HideAndDontSave;
            return Sprite.Create(tex, new Rect(0, 0, w, 2), new Vector2(0.5f, 0.5f), 100f);
        }

        static void Build(Hud h)
        {
            MessageHud mh = MessageHud.instance;
            TMP_Text like = mh != null && mh.m_messageText != null ? mh.m_messageText : h.m_hoverName;
            if (mh != null && mh.m_messageIcon != null) { float s = mh.m_messageIcon.rectTransform.rect.height; if (s > 8f && s < 128f) iconSize = Mathf.Round(s * IconScale); }
            Image dark = null;
            foreach (Image im in h.m_staminaBar2Root != null ? h.m_staminaBar2Root.GetComponentsInChildren<Image>(true) : new Image[0]) if (im.name == "darken") { dark = im; break; }
            if (dark != null) bandColor = dark.color;
            bandColor.a = Mathf.Max(bandColor.a, BandAlpha);   // darker than the game's tone at its lightest, so the band reads as a backing
            rampOne = Ramp(false);
            if (h.m_gpCooldown != null) highlight = h.m_gpCooldown.color;
            if (like != null) plain = like.color;
            root = Hints.New("ControllerPro_PickupFeed", h.m_rootObject.transform);
            Hints.Place(root, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            for (int i = 0; i < Max; i++)
            {
                Row r = new Row();
                GameObject go = Hints.New("row" + i, root.transform);
                r.rt = Hints.Place(go, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(400f, iconSize + 2f * Pad));
                r.band = Hints.Img(Hints.New("band", go.transform), rampOne, bandColor);
                r.band.raycastTarget = false;
                /* the quick white flash on the band when the row comes in (or its count goes up): the band's own fade, in white */
                r.flash = Hints.Img(Hints.New("flash", go.transform), rampOne, Color.clear);
                r.icon = Hints.Img(Hints.New("icon", go.transform), null, Color.white);
                r.icon.preserveAspect = true; r.icon.raycastTarget = false;
                r.text = Hints.Text(Hints.New("text", go.transform), like, (like != null ? like.fontSize : 18f) * TextScale, plain, TextAlignmentOptions.MidlineRight);
                if (like != null) { r.text.fontSharedMaterial = like.fontSharedMaterial; r.text.fontStyle = like.fontStyle; }   // its outline
                r.text.enableWordWrapping = false; r.text.raycastTarget = false;
                rows[i] = r;
            }
            Plugin.Log.LogInfo("Pickup feed: text like '" + (like != null ? like.name : "-") + "' size " + (like != null ? like.fontSize : 0f) + ", icon " + iconSize + ", highlight " + highlight);
        }

        // the screen box of what is drawn under t (enabled graphics only), or false when nothing is
        static bool Drawn(Transform t, out Rect r)
        {
            r = new Rect();
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            CanvasGroup g = t.GetComponent<CanvasGroup>();
            if (g != null && g.alpha < 0.05f) return false;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (Graphic gr in t.GetComponentsInChildren<Graphic>(false))
            {
                if (!gr.enabled || gr.color.a < 0.02f || gr.canvasRenderer.GetAlpha() < 0.02f) continue;
                if (root != null && gr.transform.IsChildOf(root.transform)) continue;
                gr.rectTransform.GetWorldCorners(c4);
                x0 = Mathf.Min(x0, c4[0].x); y0 = Mathf.Min(y0, c4[0].y); x1 = Mathf.Max(x1, c4[2].x); y1 = Mathf.Max(y1, c4[2].y);
            }
            if (x0 == float.MaxValue) return false;
            r = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        // the room the feed has: its bottom (floorY), the most it may reach up to (ceilY), and its right edge (edgeX)
        static void Room(Hud h, float scale)
        {
            float clear = Clear * scale, W = Screen.width, H = Screen.height;
            List<Transform> parts = new List<Transform>();
            List<Transform> mapStack = new List<Transform>();   // the minimap and its button guide: kept further off (MapClear)
            Minimap mm = Minimap.instance;
            if (mm != null && mm.m_mapImageSmall != null && !MapPlace.Hidden) { parts.Add(mm.m_mapImageSmall.transform); mapStack.Add(mm.m_mapImageSmall.transform); }
            KeyHints kh = KeyHints.instance;
            if (kh != null) foreach (GameObject hg in new GameObject[] { kh.m_combatHints, kh.m_buildHints }) if (hg != null) { parts.Add(hg.transform); mapStack.Add(hg.transform); }
            RectTransform bar = Favorites.HudBox;
            if (bar != null) parts.Add(bar);
            if (h.m_statusEffectListRoot != null) parts.Add(h.m_statusEffectListRoot);
            if (Buffs.Box != null) parts.Add(Buffs.Box);
            float edge = 30f * scale;
            if (mm != null && mm.m_mapImageSmall != null && mm.m_mapImageSmall.gameObject.activeInHierarchy) { mm.m_mapImageSmall.rectTransform.GetWorldCorners(c4); float g = W - c4[2].x; if (g >= 0f && g < W * 0.2f) edge = g; }
            edgeX = W - edge;
            floorY = edge; ceilY = H - edge;
            foreach (Transform t in parts)
            {
                Rect r;
                if (!Drawn(t, out r)) continue;
                if (r.xMax <= W * 0.7f) continue;   // only what is on the right side
                float cl = mapStack.Contains(t) ? MapClear * scale : clear;
                if (r.center.y < H * 0.5f) floorY = Mathf.Max(floorY, r.yMax + cl);   // below: the feed stands on it
                else ceilY = Mathf.Min(ceilY, r.yMin - cl);              // above: the feed stops under it
            }
        }

        public static void Tick()
        {
            Hud h = Hud.instance;
            float now = Time.unscaledTime;
            entries.RemoveAll(x => now - x.at >= Life);
            bool show = Active && Player.m_localPlayer != null && !Hud.IsUserHidden() && !Plugin.BagCovers && !Minimap.IsOpen() && entries.Count > 0;
            if (!Active) entries.Clear();
            if (!show) { if (root != null && root.activeSelf) root.SetActive(false); return; }
            if (root == null || root.transform.parent == null) Build(h);
            if (!root.activeSelf) root.SetActive(true);
            root.transform.SetAsLastSibling();
            float scale = Mathf.Max(0.01f, root.transform.lossyScale.y);
            if (now - measuredAt >= Measure || measuredAt < 0f) { measuredAt = now; Room(h, scale); }
            float rowPx = (iconSize + 2f * Pad) * scale, gapPx = Gap * scale;
            int fit = Mathf.Clamp(Mathf.FloorToInt((ceilY - floorY + gapPx) / (rowPx + gapPx)), 0, Max);
            int count = Mathf.Min(fit, entries.Count);
            for (int i = 0; i < Max; i++)
            {
                Row r = rows[i];
                bool on = i < count;
                if (r.rt.gameObject.activeSelf != on) r.rt.gameObject.SetActive(on);
                if (!on) continue;
                Entry e = entries[entries.Count - 1 - i];   // row 0 is the bottom: the newest
                string txt = e.name + "  x" + e.n;
                if (r.shown != txt)
                {
                    r.shown = txt; r.text.text = txt; r.icon.sprite = e.icon;
                    float tw = r.text.GetPreferredValues(txt).x;
                    Vector2 rm = new Vector2(1f, 0.5f);
                    // the name, then the icon at the right edge
                    Hints.Place(r.icon.gameObject, rm, rm, rm, new Vector2(-Pad, 0f), new Vector2(iconSize, iconSize));
                    r.text.alignment = TextAlignmentOptions.MidlineRight;
                    Hints.Place(r.text.gameObject, rm, rm, rm, new Vector2(-(Pad + iconSize + IconGap), 0f), new Vector2(360f, iconSize));
                    r.bandW = Pad + iconSize + IconGap + tw + Pad + BandFade;
                    Hints.Place(r.band.gameObject, rm, rm, rm, Vector2.zero, new Vector2(r.bandW, iconSize + 2f * Pad));
                }
                Color col = i == 0 ? highlight : plain;
                // a new row (or one brought back to the bottom) slides in from the right as it fades in, easing out
                float tIn = Mathf.Clamp01((now - e.at) / SlideIn), eIn = 1f - (1f - tIn) * (1f - tIn) * (1f - tIn);
                float a = Mathf.Min(Mathf.Clamp01((Life - (now - e.at)) / Fade), eIn);
                col.a *= a;
                r.text.color = col;
                r.icon.color = new Color(1f, 1f, 1f, a);
                Color bc = bandColor; bc.a *= a; r.band.color = bc;
                // the band runs on to the screen's right edge, the content staying where it is: no gap at the edge
                float toEdge = Mathf.Max(0f, (Screen.width - edgeX) / scale);
                RectTransform bt = r.band.rectTransform;
                Vector2 want = new Vector2(toEdge, 0f), size = new Vector2(r.bandW + toEdge, iconSize + 2f * Pad);
                if (bt.anchoredPosition != want) bt.anchoredPosition = want;
                if (bt.sizeDelta != size) bt.sizeDelta = size;
                // the flash: the bag's landed flash (the favorites' timing, 0.3 s, white at 32% settling back), on the band
                RectTransform ft = r.flash.rectTransform;
                ft.anchorMin = bt.anchorMin; ft.anchorMax = bt.anchorMax; ft.pivot = bt.pivot;
                if (ft.anchoredPosition != want) ft.anchoredPosition = want;
                if (ft.sizeDelta != size) ft.sizeDelta = size;
                float fd = Mathf.Max(0.05f, Plugin.FavFlash.Value > 0f ? Plugin.FavFlash.Value : 0.3f), ftk = Mathf.Clamp01((now - e.at) / fd);
                r.flash.color = new Color(1f, 1f, 1f, 0.32f * (1f - ftk) * (1f - ftk));
                r.rt.pivot = new Vector2(1f, 0f);
                r.rt.position = new Vector3(edgeX + SlideDist * scale * (1f - eIn), floorY + i * (rowPx + gapPx), r.rt.position.z);
            }
        }
    }

    // While the inventory is open on the controller, the rest of the HUD (minimap, health, food, Forsaken power, status
    // effects) fades out: none of it can be used from there, and it showed through and under the panels and the button
    // guide. Done after the game's own layout each frame, and undone the moment the inventory closes or keyboard or mouse
    // takes over. (Until 0.53.0 this also moved the inventory down for a tab bar at the top; both were removed for the
    // game's own layout.)
    static class HudBehind
    {
        public static void Tick()
        {
            if (InventoryGui.instance == null) return;
            bool open = Plugin.BagCovers && Plugin.Enabled.Value && ZInput.IsGamepadActive();
            HudFade(open && Plugin.HideHud.Value);
        }
        // ----- the HUD behind the inventory -----
        // Piece by piece rather than the whole HUD at once, so the game's own hint rows and anything else that shares
        // the HUD's root are left alone.
        class Faded { public GameObject go; public CanvasGroup group; public float was = 1f; public bool held, vital; }
        static readonly System.Collections.Generic.List<Faded> pieces = new System.Collections.Generic.List<Faded>();
        static Hud builtFor;
        static float shown = 1f, shownVital = 1f;

        static bool addVital;
        static void Add(GameObject go)
        {
            if (go == null) return;
            foreach (Faded have in pieces) if (have.go == go) return;
            Faded f = new Faded();
            f.go = go; f.vital = addVital;
            f.group = go.GetComponent<CanvasGroup>();
            if (f.group == null) { f.group = go.AddComponent<CanvasGroup>(); f.group.blocksRaycasts = true; f.group.interactable = true; }
            pieces.Add(f);
        }
        static void Add(Component c) { if (c != null) Add(c.gameObject); }
        static void AddField(Hud hud, string field)
        {
            FieldInfo f = AccessTools.Field(typeof(Hud), field);
            object v = f != null ? f.GetValue(hud) : null;
            Component c = v as Component; GameObject g = v as GameObject;
            if (c != null) Add(c.gameObject); else if (g != null) Add(g);
        }

        static void HudFade(bool hide)
        {
            Hud hud = Hud.instance;
            if (hud == null) return;
            if (builtFor != hud)
            {
                builtFor = hud;
                pieces.Clear();
                // health sits inside a panel of its own when there is one
                // Vitals: health, food, stamina and the Forsaken power beside them. These stay while only the bag is
                // open, since that is where you eat and want to watch them climb. They go when a chest is open,
                // because the chest and its button guide come down over that corner.
                addVital = true;
                if (hud.m_healthPanel != null) Add(hud.m_healthPanel); else Add(hud.m_healthBarRoot);
                Add(hud.m_foodBarRoot); Add(hud.m_gpRoot);
                Add(hud.m_staminaBar2Root); Add(hud.m_eitrBarRoot); Add(hud.m_adrenalineBarRoot);
                addVital = false;
                Add(hud.m_statusEffectListRoot);
                // the crosshair and the "Chest / Open" text in the middle of the screen
                AddField(hud, "m_crosshair"); AddField(hud, "m_crosshairBow"); AddField(hud, "m_hoverName");
                if (Minimap.instance != null) Add(Minimap.instance.m_smallRoot);
            }
            float step = Time.unscaledDeltaTime / 0.12f;
            bool chest = InventoryGui.instance != null && InventoryGui.instance.IsContainerOpen();
            /* going away is instant, so the whole HUD leaves in one frame with the mod's own pieces; coming back still fades in */
            shown = hide ? 0f : Mathf.MoveTowards(shown, 1f, step);
            shownVital = hide && chest ? 0f : Mathf.MoveTowards(shownVital, 1f, step);
            foreach (Faded f in pieces)
            {
                if (f.go == null || f.group == null) continue;
                // A display the grouped resource bars hide is the group's alone while it does: not faded or recorded here.
                // Only those: the Forsaken power (moved, never hidden), adrenaline, and in Mixed the game's own stamina and eitr
                // keep their fade here. Skipped too, they were left faint whenever the group took over mid-fade (closing a
                // chest, switching layouts).
                if (Vitals.HidesNow(f.go)) { f.held = false; continue; }
                // the minimap switched off in the settings is the map setting's alone, in the same way
                if (MapPlace.Hidden && Minimap.instance != null && f.go == Minimap.instance.m_smallRoot) { f.held = false; continue; }
                // the game's buffs and debuffs hidden for the slim boxes are theirs alone, in the same way
                if (Buffs.Hiding && hud.m_statusEffectListRoot != null && f.go == hud.m_statusEffectListRoot.gameObject) { f.held = false; continue; }
                float a = f.vital ? shownVital : shown;
                if (a >= 1f)
                {
                    // back to how the game had it. A display the game animates (stamina, eitr, health) is shown fully and its
                    // show/hide logic started over, since a value recorded mid-fade would stick: the game only fades it again
                    // when its state changes.
                    if (f.held) { Animator an = f.group.GetComponent<Animator>(); if (an != null && an.isActiveAndEnabled) { f.group.alpha = 1f; an.Rebind(); an.Update(0f); } else f.group.alpha = f.was; f.held = false; }
                    continue;
                }
                if (!f.held) { f.was = f.group.alpha; f.held = true; }
                f.group.alpha = f.was * a;
            }
        }
    }
    // ---------- dimming the panels that are not in use ----------
    // On the controller, every inventory panel except the active one is darkened, so the eye goes to the panel the
    // buttons act on. Nothing of the game's is changed: each panel gets a black layer on top, cut to the shape of the
    // panel's own art (and of the small armor and weight tabs that hang off it), and that layer fades in and out.
    static class Dim
    {
        class Panel
        {
            public RectTransform rt; public GameObject root; public CanvasGroup group; public Transform frame; public float a;
            public readonly System.Collections.Generic.List<RectTransform> src = new System.Collections.Generic.List<RectTransform>(), dst = new System.Collections.Generic.List<RectTransform>();
        }
        const float FadeSeconds = 0.12f;
        static readonly Panel[] panels = new Panel[4];
        static readonly RectTransform[] rts = new RectTransform[4];
        static int current = -1;
        public static int Current { get { return current; } }
        public static RectTransform CurrentPanel { get { return current >= 0 ? rts[current] : null; } }   // 0 bag, 1 chest, 2 crafting, 3 the info panel; -1 not known yet
        static bool showing;

        public static void Clear()
        {
            if (!showing) return;
            showing = false; current = -1;
            foreach (Panel p in panels)
                if (p != null && p.root != null) { p.a = 0f; p.group.alpha = 0f; p.root.SetActive(false); }
        }

        static void AddPiece(Panel p, Transform art)
        {
            Image from = art != null ? art.GetComponent<Image>() : null;
            if (from == null || from.sprite == null) return;
            Image im = Hints.Img(Hints.New("piece", p.root.transform), from.sprite, Color.black);
            im.type = from.type; im.pixelsPerUnitMultiplier = from.pixelsPerUnitMultiplier; im.preserveAspect = from.preserveAspect;
            p.src.Add(from.rectTransform); p.dst.Add(im.rectTransform);
        }

        static Panel Get(int i)
        {
            RectTransform rt = rts[i];
            Panel p = panels[i];
            if (rt == null) return null;
            if (p != null && p.root != null && p.rt == rt) return p;
            Vector2 mid = new Vector2(0.5f, 0.5f);
            p = new Panel();
            p.rt = rt; p.frame = rt.Find("selected_frame");
            p.root = Hints.New("ControllerPro_Dim", rt);
            Hints.Place(p.root, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
            p.group = p.root.AddComponent<CanvasGroup>();
            p.group.blocksRaycasts = false; p.group.interactable = false; p.group.alpha = 0f;
            Transform art = rt.Find("Bkg");
            if (art == null) art = rt.Find("bkg");
            AddPiece(p, art);
            foreach (Transform child in rt)
            {
                if (child == p.root.transform || Array.IndexOf(rts, child as RectTransform) >= 0) continue;   // the chest panel lives inside the bag panel
                AddPiece(p, child.Find("bkg"));                       // the armor and weight tabs
                if (child.name == "RepairSimple") AddPiece(p, child);  // the repair tab on the side of the crafting panel
            }
            if (p.src.Count == 0) Plugin.Log.LogInfo("Panel dimming: no background art found on '" + rt.name + "', it will stay as it is");
            p.root.SetActive(false);
            panels[i] = p;
            return p;
        }

        // the layer follows the art it darkens, wherever the game puts it
        static void Follow(Panel p)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f);
            Vector3 rs = p.root.transform.lossyScale;
            for (int i = 0; i < p.src.Count; i++)
            {
                RectTransform s = p.src[i], d = p.dst[i];
                if (s == null || d == null) continue;
                bool on = s.gameObject.activeInHierarchy;
                if (d.gameObject.activeSelf != on) d.gameObject.SetActive(on);
                if (!on) continue;
                Vector3 ss = s.lossyScale;
                d.anchorMin = d.anchorMax = mid; d.pivot = s.pivot; d.sizeDelta = s.rect.size;
                d.position = s.position; d.rotation = s.rotation;
                d.localScale = new Vector3(rs.x != 0f ? ss.x / rs.x : 1f, rs.y != 0f ? ss.y / rs.y : 1f, 1f);
            }
        }

        // Which panel is in use: the one the cursor is over, or failing that the one the game's own focus is on.
        static int Find(InventoryGui gui)
        {
            if (Cursor.On)
            {
                if (current >= 0 && Cursor.OverPanel(rts[current])) return current;
                for (int i = 0; i < 4; i++) if (Cursor.OverPanel(rts[i])) return i;
                return -1;   // in the open space between panels: stay as it was
            }
            InventoryGrid pg = gui.m_playerGrid, cg = gui.IsContainerOpen() ? gui.ContainerGrid : null;
            if (pg != null && pg.m_uiGroup != null && pg.m_uiGroup.IsActive) return 0;
            if (cg != null && cg.m_uiGroup != null && cg.m_uiGroup.IsActive) return 1;
            // the other panels: the game switches on a panel's "selected" frame while its focus is there
            for (int i = 2; i < 4; i++)
                if (panels[i] != null && panels[i].frame != null && panels[i].frame.gameObject.activeInHierarchy) return i;
            return -1;
        }

        public static void Apply(InventoryGui gui)
        {
            rts[0] = gui.m_player; rts[1] = gui.m_container; rts[2] = gui.m_crafting; rts[3] = gui.m_info;
            for (int i = 0; i < 4; i++) Get(i);
            int now = Find(gui);
            if (now >= 0) current = now;
            showing = true;
            if (!Plugin.DimPanels.Value)
            {
                foreach (Panel off in panels) if (off != null && off.root != null && off.root.activeSelf) { off.a = 0f; off.root.SetActive(false); }
                return;
            }
            float strength = Mathf.Clamp01(Plugin.DimStrength.Value), step = Time.unscaledDeltaTime / FadeSeconds;
            bool pair = gui.IsContainerOpen();
            for (int i = 0; i < 4; i++)
            {
                Panel p = panels[i];
                if (p == null || p.root == null) continue;
                // With a chest open, the bag and the chest count as one: moving items is done across both, so both stay
                // at full strength whichever of the two has the focus.
                bool together = pair && i <= 1 && current <= 1;
                bool dim = current >= 0 && i != current && !together && p.rt.gameObject.activeInHierarchy;
                p.a = Mathf.MoveTowards(p.a, dim ? 1f : 0f, step);
                bool on = p.a > 0.001f;
                if (p.root.activeSelf != on) p.root.SetActive(on);
                if (!on) continue;
                p.root.transform.SetAsLastSibling();
                Follow(p);
                p.group.alpha = p.a * strength;
            }
        }
    }

    // ---------- the item tooltip in the inventory, on the controller ----------
    // The game hangs the tooltip from the top of the focused slot and pushes it back on screen. For a slot low down (a
    // chest, a mannequin's slots) a tall tooltip ran off the bottom or over the button guide. Its top now always lines
    // up with the top of the bag, where it already sits for the bag's first row; across, it stays where the game put it.
    [HarmonyPatch(typeof(UITooltip), "AnchorTooltip")]
    static class Patch_TooltipTop
    {
        static readonly FieldInfo F_tip = AccessTools.Field(typeof(UITooltip), "m_tooltip");
        static readonly FieldInfo F_current = AccessTools.Field(typeof(UITooltip), "m_current");
        static readonly Vector3[] c = new Vector3[4];
        static void Postfix(UITooltip __instance)
        {
            try
            {
                if (!Plugin.Enabled.Value || !ZInput.IsExclusiveGamepadActive() || !InventoryGui.IsVisible()) return;
                InventoryGui gui = InventoryGui.instance;
                if (gui == null || gui.m_player == null || F_tip == null || F_current == null) return;
                if (!ReferenceEquals(F_current.GetValue(null), __instance)) return;
                GameObject tip = F_tip.GetValue(null) as GameObject;
                if (tip == null || tip.transform.childCount == 0) return;
                // only the bag's and the chest panel's slots (the chest panel also shows a mannequin's or stand's slots)
                Transform t = __instance.transform;
                if (!t.IsChildOf(gui.m_player) && (gui.m_container == null || !t.IsChildOf(gui.m_container))) return;
                RectTransform box = tip.transform.GetChild(0) as RectTransform;
                if (box == null) return;
                gui.m_player.GetWorldCorners(c);
                float top = c[1].y;
                box.GetWorldCorners(c);
                float dy = top - c[1].y;
                if (Mathf.Abs(dy) < 0.5f) return;
                if (c[0].y + dy < 0f) dy = -c[0].y;   // a tooltip taller than the space under the bag's top still stays on screen
                tip.transform.position += new Vector3(0f, dy, 0f);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Tooltip placement: " + e.Message); }
        }
    }

    // ---------- putting items on mannequins, item stands, item frames and altars from the bag ----------
    // On the controller, using one of these opens the bag with the object's own slots under it, where a chest's would be,
    // in the chest panel's look. RT on a bag item puts it on the object at once (as RT sends an item into a chest), RT on
    // one of the object's slots takes it back, and A carrying a bag item onto the slots puts it on too. Nothing is confirmed.
    // Every check and every move is the object's own game code, the same as using a hotbar item on it: wards, multiplayer
    // ownership, one item per slot and the altar's count all stay the game's, and if the game refuses, nothing moves.
    [HarmonyPatch(typeof(Player), "Interact")]
    static class Patch_PlayerInteract
    {
        static bool Prefix(Player __instance, GameObject go, bool hold, bool alt)
        {
            try
            {
                if (__instance != Player.m_localPlayer || alt) return true;
                if (!hold && (Deposit.TryFire(go) || Deposit.TryOpen(go))) return false;
                if (hold && Deposit.Opens(go)) return false;   // holding X does nothing here: X opens, and taking is done in the panel
            }
            catch (Exception e) { Plugin.Log.LogWarning("Object slots: " + e.Message); }
            return true;
        }
    }

    // The object's slots are shown in the game's own chest panel, as a chest's would be: same art, same grid, same
    // focus, cursor and buttons. The panel holds a stand-in inventory, a picture of what is on the object, laid out in
    // the object's slot order (a mannequin with 14 places shows 14 slots, an item stand or an altar one). Nothing ever
    // really moves into it: every way an item could go in or out (A, carrying, RT, X, the cursor, the mouse, the split
    // window, stack-all and take-all) is caught and turned into the object's own put-on or take-off.
    [HarmonyPatch(typeof(InventoryGui), "IsContainerOpen")]
    static class Patch_IsContainerOpen
    {
        static void Postfix(ref bool __result) { if (Deposit.Active) __result = true; }
    }

    [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
    static class Patch_UpdateContainer
    {
        static bool Prefix(InventoryGui __instance, Player player)
        {
            if (!Deposit.Active) return true;
            try { Deposit.ShowPanel(__instance, player); } catch (Exception e) { Plugin.Log.LogWarning("Object slots: " + e.Message); Deposit.Close(); }
            return false;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
    static class Patch_OnSelectedItem
    {
        static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, InventoryGrid.Modifier mod)
        {
            if (!Deposit.Active) return true;
            try { return !Deposit.Selected(__instance, grid, item, mod); }
            catch (Exception e) { Plugin.Log.LogWarning("Object slots: " + e.Message); return false; }
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
    static class Patch_OnRightClickItem
    {
        static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item)
        {
            if (!Deposit.Active) return true;
            if (grid == __instance.m_playerGrid) return !Deposit.Locked(item);   // a greyed bag item cannot be equipped or used either
            if (grid != __instance.ContainerGrid) return true;
            if (item != null) Deposit.TakeItem(item);
            return false;
        }
    }

    // the prompt over a mannequin, an item stand or an altar reads "[X] Open", as on a chest
    [HarmonyPatch(typeof(Switch), "GetHoverText")]
    static class Patch_SwitchHover
    {
        static void Postfix(Switch __instance, ref string __result)
        {
            try
            {
                string fire = Deposit.FireHover(__instance.gameObject, __result);   // a catapult holding its load: "[X] Fire"
                __result = fire ?? Deposit.Hover(__instance.gameObject, __result);
            }
            catch { }
        }
    }
    [HarmonyPatch(typeof(ItemStand), "GetHoverText")]
    static class Patch_ItemStandHover { static void Postfix(ItemStand __instance, ref string __result) { try { __result = Deposit.Hover(__instance.gameObject, __result); } catch { } } }
    [HarmonyPatch(typeof(OfferingBowl), "GetHoverText")]
    static class Patch_OfferingBowlHover { static void Postfix(OfferingBowl __instance, ref string __result) { try { __result = Deposit.Hover(__instance.gameObject, __result); } catch { } } }

    [HarmonyPatch(typeof(InventoryGui), "OnTakeAll")]
    static class Patch_OnTakeAll { static bool Prefix() { return !Deposit.Active; } }

    [HarmonyPatch(typeof(InventoryGui), "OnStackAll")]
    static class Patch_OnStackAll { static bool Prefix() { return !Deposit.Active; } }

    static class Deposit
    {
        public static bool Active;
        public static Inventory Inv;   // the stand-in shown in the chest panel
        static ArmorStand armor;
        static ItemStand stand;
        static OfferingBowl bowl;
        static Vector3 at;
        static bool focusPending, first, slotsLogged;
        static int width = 1;
        static int[] shown = new int[0];
        static readonly MethodInfo M_ArmorCanAttach = AccessTools.Method(typeof(ArmorStand), "CanAttach");
        static readonly MethodInfo M_StandCanAttach = AccessTools.Method(typeof(ItemStand), "CanAttach");
        static readonly FieldInfo F_standView = AccessTools.Field(typeof(ItemStand), "m_nview");
        static readonly FieldInfo F_armorView = AccessTools.Field(typeof(ArmorStand), "m_nview");
        static readonly FieldInfo F_dragItem = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        static readonly FieldInfo F_dragGo = AccessTools.Field(typeof(InventoryGui), "m_dragGo");
        static readonly FieldInfo F_current = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");
        static readonly MethodInfo M_SetupDragItem = AccessTools.Method(typeof(InventoryGui), "SetupDragItem");
        static readonly Color Greyed = new Color(0.5f, 0.5f, 0.5f, 0.55f);   // the game's grey for an item it will not use here, a little faded

        static Component Target() { return cat != null ? (Component)cat : armor != null ? (Component)armor : stand != null ? (Component)stand : (Component)bowl; }
        static Catapult cat, found;   // the open catapult, and the one the last Resolve found

        // The bag closes by itself when the catapult is loaded or an offering is made, with RT (or A) still down. The game
        // would read that button as held in the world, a heavy attack (or a jump). Until both are let go, it reads nothing.
        // Each button is followed on its own, every frame: it counts again from the frame after it is let go, so a fresh
        // press (A to jump right after loading with RT) always works.
        public static bool HeldThrough;
        static bool rtHeld, aHeld;
        static int rtFreeFrame = -10, aFreeFrame = -10;
        static void CloseHeld()
        {
            rtHeld = Core.RawHeld("JoyRTrigger");
            aHeld = Core.RawHeld("JoyButtonA");
            HeldThrough = rtHeld || aHeld;
            if (InventoryGui.instance != null) InventoryGui.instance.Hide();
        }
        // once a frame, from the plugin's update
        public static void Tick()
        {
            if (!HeldThrough) return;
            if (rtHeld && !Core.RawHeld("JoyRTrigger")) { rtHeld = false; rtFreeFrame = Time.frameCount; }
            if (aHeld && !Core.RawHeld("JoyButtonA")) { aHeld = false; aFreeFrame = Time.frameCount; }
            HeldThrough = rtHeld || aHeld;
        }
        // A catapult loaded from the bag holds its load: the game's own timed shot is called off, and X on the loading
        // point fires it with the game's own shot. (Loaded the vanilla way, from the hotbar, it fires by itself as usual.)
        static readonly HashSet<Catapult> holding = new HashSet<Catapult>();
        static readonly MethodInfo M_CatShoot = AccessTools.Method(typeof(Catapult), "Shoot");
        static Catapult Held(GameObject go)
        {
            if (go == null || holding.Count == 0) return null;
            Switch sw = go.GetComponentInParent<Switch>();
            Catapult k = sw != null ? sw.GetComponentInParent<Catapult>() : null;
            if (k == null || k.m_loadPoint != sw || !holding.Contains(k)) return null;
            if (F_catLoaded == null || F_catLoaded.GetValue(k) == null) { holding.Remove(k); return null; }   // already gone (fired, or reloaded the game)
            return k;
        }
        // X on a held catapult: fire. Works with any input, so a held load never gets stuck.
        public static bool TryFire(GameObject go)
        {
            Catapult k = Held(go);
            if (k == null || M_CatShoot == null) return false;
            holding.Remove(k);
            M_CatShoot.Invoke(k, null);
            return true;
        }
        public static string FireHover(GameObject go, string text)
        {
            if (Held(go) == null) return null;
            return Localize("[<color=yellow><b>$KEY_Use</b></color>] ") + "Fire";
        }

        public static bool Swallow(string name)
        {
            bool rt = name == "JoyRTrigger" || name == "JoyAttack" || name == "JoySecondaryAttack";
            bool a = name == "JoyButtonA" || name == "JoyJump";
            if (rt) return rtHeld || Time.frameCount == rtFreeFrame;   // still down, or the frame it was let go (its release does not count)
            if (a) return aHeld || Time.frameCount == aFreeFrame;
            return false;
        }
        static readonly MethodInfo M_CatCanLoad = AccessTools.Method(typeof(Catapult), "CanItemBeLoaded");
        static readonly FieldInfo F_catLoaded = AccessTools.Field(typeof(Catapult), "m_loadedItem");
        static readonly FieldInfo F_catArm = AccessTools.Field(typeof(Catapult), "m_armAnimTime");
        // loaded, or still swinging from the last shot: the game takes nothing until it is back
        static bool CatBusy()
        {
            if (F_catLoaded.GetValue(cat) != null) return true;
            object t = F_catArm.GetValue(cat);
            return t is float && (float)t != 0f;
        }

        // Which object, if any, using this would open. The same answer drives the prompt and the opening.
        static bool Resolve(GameObject go, out ArmorStand a, out ItemStand s, out OfferingBowl b)
        {
            a = null; s = null; b = null; found = null;
            if (go == null || !Plugin.Enabled.Value || !Plugin.ObjectSlots.Value || !ZInput.IsGamepadActive()) return false;
            if (M_ArmorCanAttach == null || M_StandCanAttach == null || F_standView == null || F_current == null) return false;
            Switch sw = go.GetComponentInParent<Switch>();
            if (sw != null)
            {
                // the catapult: its loading point opens; its legs (the bracing switch) stay the game's
                Catapult k = sw.GetComponentInParent<Catapult>();
                if (k != null)
                {
                    if (k.m_loadPoint != sw || M_CatCanLoad == null || F_catLoaded == null || F_catArm == null) return false;
                    found = k;
                    return true;
                }
                a = sw.GetComponentInParent<ArmorStand>();
                if (a != null && SlotOf(a, sw) < 0) { a = null; return false; }   // the mannequin's pose switch stays the game's
            }
            if (a == null)
            {
                s = go.GetComponentInParent<ItemStand>();
                if (s != null && s.m_guardianPower != null) { s = null; return false; }   // guardian stones stay the game's
            }
            if (a == null && s == null)
            {
                b = go.GetComponentInParent<OfferingBowl>();
                if (b != null && (b.m_useItemStands || b.m_bossItem == null)) { b = null; return false; }   // altars run from item stands: those stands open, the altar stays the game's
            }
            return a != null || s != null || b != null;
        }

        // The prompt over the object, on the controller: the game's own lines for taking and attaching ("[D-pad] Attach")
        // become one "[X] Open", as on a chest. Other lines (its name, rotating with a combination) stay.
        public static string Hover(GameObject go, string text)
        {
            ArmorStand a; ItemStand s; OfferingBowl b;
            if (string.IsNullOrEmpty(text) || !Resolve(go, out a, out s, out b)) return text;
            string use = Localize("$KEY_Use"), bar = Localize("$KEY_HotbarUse"), holdWord = Localize("$ui_hold");
            string take = Localize("$piece_itemstand_take"), attach = Localize("$piece_itemstand_attach");
            string[] lines = text.Split('\n');
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            bool opened = false;
            foreach (string l in lines)
            {
                // any take, attach or hold line (the item stand's "Hold X: Take" has no brackets); the rotate line ("+") stays
                bool action = !l.Contains("+") && (l.Contains("$KEY_HotbarUse") || l.Contains("$KEY_Use") || l.Contains("$ui_hold")
                    || (bar.Length > 0 && l.Contains(bar)) || (use.Length > 0 && l.Contains(use)) || (holdWord.Length > 1 && l.Contains(holdWord))
                    || (take.Length > 1 && !take.StartsWith("$") && l.Contains(take)) || (attach.Length > 1 && !attach.StartsWith("$") && l.Contains(attach)));
                if (action)
                {
                    if (opened) continue;
                    opened = true;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(Localize("[<color=yellow><b>$KEY_Use</b></color>] $piece_container_open"));
                    continue;
                }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(l);
            }
            return opened ? sb.ToString() : text;
        }

        public static bool Opens(GameObject go) { ArmorStand a; ItemStand s; OfferingBowl b; return Resolve(go, out a, out s, out b); }

        public static bool TryOpen(GameObject go)
        {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null || InventoryGui.IsVisible() || gui.ContainerGrid == null) return false;
            ArmorStand a; ItemStand s; OfferingBowl b;
            if (!Resolve(go, out a, out s, out b)) return false;
            HeldThrough = false;
            Catapult k = found;
            Component c = k != null ? (Component)k : a != null ? (Component)a : s != null ? (Component)s : (Component)b;
            // no access: the game says so, as usual (the catapult makes no such check of its own, so none is made here)
            if (k == null && !PrivateArea.CheckAccess(c.transform.position, 0f, false, false)) return false;
            armor = a; stand = s; bowl = b; cat = k;
            at = c.transform.position;
            if (a != null) FindPlaces();
            // as many slots as the object has places the game can use (the mannequin: 7), in rows as wide as fit them evenly
            int n = Count();
            width = n;
            if (n > 8) { width = 8; for (int w = 8; w >= 5; w--) if (n % w == 0) { width = w; break; } }
            string title;
            if (k != null) { Piece pc = k.GetComponentInParent<Piece>(); title = pc != null ? pc.m_name : "Catapult"; }
            else title = a != null ? a.m_name : s != null ? s.m_name : b.m_name;
            Inv = new Inventory(Localize(title), null, width, (n + width - 1) / width);
            shown = new int[0];
            Active = true; focusPending = true; first = true;
            gui.Show(null, 1);
            Plugin.Log.LogInfo("Object slots: opened " + (k != null ? "catapult" : a != null ? "mannequin" : s != null ? "item stand" : "altar") + ", " + n + " slots, " + width + " wide");
            if (a != null && !slotsLogged)
            {
                // once a game: every place the mannequin defines, what kind it is, what it takes, and whether it is switched on
                slotsLogged = true;
                for (int i = 0; i < a.m_slots.Count; i++)
                {
                    ArmorStand.ArmorStandSlot sl = a.m_slots[i];
                    string types = "";
                    if (sl.m_supportedTypes != null) foreach (ItemDrop.ItemData.ItemType t in sl.m_supportedTypes) types += (types.Length > 0 ? "," : "") + t;
                    Plugin.Log.LogInfo("Mannequin slot " + i + ": " + sl.m_slot + " takes [" + (types.Length > 0 ? types : "anything") + "] switch " + (sl.m_switch != null ? sl.m_switch.name + (sl.m_switch.gameObject.activeInHierarchy ? " on" : " OFF") : "none"));
                }
            }
            return true;
        }
        static string Localize(string s) { return Localization.instance != null ? Localization.instance.Localize(s) : s; }
        static int SlotOf(ArmorStand a, Switch sw)
        {
            for (int i = 0; i < a.m_slots.Count; i++) if (a.m_slots[i].m_switch == sw) return i;
            return -1;
        }

        public static void Close()
        {
            if (!Active) return;
            Active = false; armor = null; stand = null; bowl = null; cat = null; Inv = null;
            InventoryGui gui = InventoryGui.instance;
            // the chest panel goes back to the game: hidden, unless a real chest is open
            if (gui != null && gui.m_container != null && F_current.GetValue(gui) == null) gui.m_container.gameObject.SetActive(false);
            if (gui != null) Buttons(gui, true);
        }
        static void Buttons(InventoryGui gui, bool on)
        {
            // (the chest's weight box stays, as on every container: ShowPanel fills it with the weight of what is on the object)
            if (gui.m_takeAllButton != null && gui.m_takeAllButton.gameObject.activeSelf != on) gui.m_takeAllButton.gameObject.SetActive(on);
            if (gui.m_stackAllButton != null && gui.m_stackAllButton.gameObject.activeSelf != on) gui.m_stackAllButton.gameObject.SetActive(on);
        }

        static int Count() { return armor != null ? places.Count : 1; }
        static bool Has(int i)
        {
            if (armor != null) return i >= 0 && i < places.Count && armor.HaveAttachment(places[i]);
            return stand != null && stand.HaveAttachment();
        }
        public static string PutLabel() { return cat != null ? "Load" : bowl != null ? "Offer" : "Deposit"; }

        // null when the item can go on the open object right now, otherwise the short reason it cannot.
        // Worked out once per item per frame: the bag asks for every slot on every update.
        static readonly Dictionary<ItemDrop.ItemData, string> whyCache = new Dictionary<ItemDrop.ItemData, string>();
        static int whyFrame = -1;
        public static string Why(ItemDrop.ItemData it)
        {
            if (it == null) return "";
            if (whyFrame != Time.frameCount) { whyFrame = Time.frameCount; whyCache.Clear(); }
            string r;
            if (whyCache.TryGetValue(it, out r)) return r;
            r = WhyNow(it);
            whyCache[it] = r;
            return r;
        }
        static string WhyNow(ItemDrop.ItemData it)
        {
            Player p = Player.m_localPlayer;
            if (p == null || !Active) return "";
            if (Inv != null && Inv.ContainsItem(it)) return "";   // one of the stand-ins: it is already on the object
            if (cat != null)
            {
                // the catapult's own check of what it can throw, and nothing while it is loaded or still swinging
                if (!Bool(M_CatCanLoad, cat, it)) return "Won't fit";
                return CatBusy() ? "Loaded" : null;
            }
            if (bowl != null)
            {
                // the altar's own check: its one offering, and enough of it in the bag
                if (bowl.m_bossItem == null || it.m_shared.m_name != bowl.m_bossItem.m_itemData.m_shared.m_name) return "Won't fit";
                return p.GetInventory().CountItems(it.m_shared.m_name) >= bowl.m_bossItems ? null : "Need " + bowl.m_bossItems;
            }
            if (stand != null)
            {
                if (!Bool(M_StandCanAttach, stand, it)) return "Won't fit";
                return stand.HaveAttachment() ? "Slot taken" : null;
            }
            if (armor != null) { int i; return ArmorSlot(it, out i); }
            return "";
        }
        static bool Bool(MethodInfo m, object o, params object[] args)
        {
            try { return (bool)m.Invoke(o, args); } catch { return false; }
        }
        // The mannequin's own rules, in its own order: an item with no attach point on its model never goes on (body and
        // leg armour excepted, they are worn as skins), then the first of its slots that takes this kind of item and is free.
        static string ArmorSlot(ItemDrop.ItemData it, out int slot)
        {
            slot = -1;
            ItemDrop.ItemData.ItemType t = it.m_shared.m_itemType;
            if (t != ItemDrop.ItemData.ItemType.Legs && t != ItemDrop.ItemData.ItemType.Chest)
            {
                bool attach = false;
                if (it.m_dropPrefab != null)
                    foreach (Transform ch in it.m_dropPrefab.transform)
                        if (ch.name == "attach" || ch.name == "attach_skin") { attach = true; break; }
                if (!attach) return "Won't fit";
            }
            // The game picks, for the switch being used, the first of its places that takes this kind of item, and if that
            // one is full the item does not go on (later places of the same kind are never used). The game's mannequin has
            // one switch, its body, so each kind of item has exactly one place.
            bool fits = false;
            List<Switch> tried = new List<Switch>();
            for (int i = 0; i < armor.m_slots.Count; i++)
            {
                Switch sw = armor.m_slots[i].m_switch;
                if (tried.Contains(sw) || !Bool(M_ArmorCanAttach, armor, armor.m_slots[i], it)) continue;
                tried.Add(sw);
                fits = true;
                if (!armor.HaveAttachment(i)) { slot = i; return null; }
            }
            return fits ? "Slot taken" : "Won't fit";
        }

        // The mannequin's places the game can actually use: it lists some twice, and only the first of each is ever
        // filled. A place that holds something is always shown, so nothing on the mannequin is hidden.
        static readonly List<int> places = new List<int>();
        static void FindPlaces()
        {
            places.Clear();
            List<string> seen = new List<string>();
            for (int i = 0; i < armor.m_slots.Count; i++)
            {
                ArmorStand.ArmorStandSlot sl = armor.m_slots[i];
                string key = (sl.m_switch != null ? sl.m_switch.GetInstanceID() : 0) + ":" + sl.m_slot;
                if (sl.m_supportedTypes != null) foreach (ItemDrop.ItemData.ItemType t in sl.m_supportedTypes) key += "," + (int)t;
                // The utility place never fills: belts and the like have no attach point on the mannequin's model.
                // So it is left out like a repeat, leaving helmet, chest, legs, cape, one weapon and one shield.
                bool repeat = seen.Contains(key) || sl.m_slot == VisSlot.Utility;
                if (!repeat) seen.Add(key);
                if (!repeat || armor.HaveAttachment(i)) places.Add(i);
            }
        }

        // Puts a bag item on the open object with the object's own code. The game takes it out of the bag once the object
        // has it; if anything refuses on the way, the item simply stays in the bag.
        public static bool Put(ItemDrop.ItemData it)
        {
            Player p = Player.m_localPlayer;
            if (!Active || p == null || it == null || !p.GetInventory().ContainsItem(it) || Why(it) != null) return false;
            bool done = false;
            if (cat != null)
            {
                // the catapult's own load: a whole stack, up to its limit. It fires a moment later, so the bag closes to show the shot.
                done = cat.m_loadPoint != null && cat.m_loadPoint.UseItem(p, it);
                if (done)
                {
                    cat.CancelInvoke("Shoot");   // it holds the load until X fires it
                    holding.Add(cat);
                    CloseHeld();
                }
            }
            else if (bowl != null)
            {
                done = bowl.UseItem(p, it);
                if (done) CloseHeld();   // the offering is made: back to the world to meet what comes
            }
            else if (stand != null) done = stand.UseItem(p, it);
            else if (armor != null)
            {
                int i;
                if (ArmorSlot(it, out i) == null) done = armor.m_slots[i].m_switch.UseItem(p, it);
            }
            whyFrame = -1;
            return done;
        }

        // Takes one of the stand-ins back off the object with the game's own take-off: the real item pops out of the
        // object and is picked up as usual.
        public static void TakeItem(ItemDrop.ItemData standIn)
        {
            if (!Active || Inv == null || standIn == null || !Inv.ContainsItem(standIn)) return;
            Take(standIn.m_gridPos.y * width + standIn.m_gridPos.x);
        }
        static void Take(int i)
        {
            Player p = Player.m_localPlayer;
            if (p == null || !Has(i) || !PrivateArea.CheckAccess(at, 0f, true, false)) return;
            if (armor != null)
            {
                // the game's own one-place drop. (Using the mannequin takes everything off at once: every place shares
                // the body switch.) The item pops out and is picked up as usual.
                ZNetView v = F_armorView != null ? F_armorView.GetValue(armor) as ZNetView : null;
                if (v != null && v.IsValid()) v.InvokeRPC("RPC_DropItem", places[i]);
            }
            else if (stand != null && stand.m_canBeRemoved)
            {
                ZNetView v = F_standView.GetValue(stand) as ZNetView;
                if (v != null && v.IsValid()) v.InvokeRPC("RPC_DropItem");
            }
            whyFrame = -1;
        }

        // Every way the game moves an item, while an object is open. True when handled here (the game's own move is skipped).
        public static bool Selected(InventoryGui gui, InventoryGrid grid, ItemDrop.ItemData item, InventoryGrid.Modifier mod)
        {
            ItemDrop.ItemData drag = F_dragItem.GetValue(gui) as ItemDrop.ItemData;
            bool carrying = (F_dragGo.GetValue(gui) as GameObject) != null && drag != null;
            // a greyed bag item is locked: it cannot be picked up, moved, dropped or swapped with what is in hand
            if (grid == gui.m_playerGrid && Locked(item)) return true;
            if (grid == gui.ContainerGrid)
            {
                // carrying a bag item onto the object's slots: it goes on, and the carry ends. Refused: it stays in hand.
                if (carrying) { if (Put(drag) && M_SetupDragItem != null) M_SetupDragItem.Invoke(gui, new object[] { null, null, 1 }); }
                // anything done to one of the object's items takes it back off
                else if (item != null && mod != InventoryGrid.Modifier.Drop) TakeItem(item);
                return true;
            }
            // from the bag, the move-across (RT, ctrl-click) puts the item on the object
            if (grid == gui.m_playerGrid && !carrying && mod == InventoryGrid.Modifier.Move && item != null) { Put(item); return true; }
            return false;
        }

        // A bag item that cannot go on the open object is locked for as long as the object is open, so it cannot be
        // picked up by mistake and dropped on the way.
        public static bool Locked(ItemDrop.ItemData it)
        {
            Player p = Player.m_localPlayer;
            return Active && it != null && p != null && p.GetInventory().ContainsItem(it) && Why(it) != null;
        }

        // The mod's own split-and-transfer goes through here too.
        public static bool Moving(Inventory from, Inventory to, ItemDrop.ItemData it)
        {
            if (!Active || Inv == null) return false;
            if (to == Inv) { Put(it); return true; }
            if (from == Inv) { TakeItem(it); return true; }
            return false;
        }

        // Once a frame while the inventory is up: closes when the object, the inventory or the controller goes away.
        public static void Frame(InventoryGui gui, bool on)
        {
            if (!Active) return;
            Component c = Target();
            Player p = Player.m_localPlayer;
            if (!on || c == null || p == null || !InventoryGui.IsVisible() || F_current.GetValue(gui) != null || Vector3.Distance(p.transform.position, at) > 6f) { Close(); return; }
            if (focusPending) { focusPending = false; FocusFirst(gui, p); }
        }
        // the focus starts on the first bag item, row by row, that can go on the object
        static void FocusFirst(InventoryGui gui, Player p)
        {
            ItemDrop.ItemData best = null;
            foreach (ItemDrop.ItemData it in p.GetInventory().GetAllItems())
            {
                if (Why(it) != null) continue;
                if (best == null || it.m_gridPos.y < best.m_gridPos.y || (it.m_gridPos.y == best.m_gridPos.y && it.m_gridPos.x < best.m_gridPos.x)) best = it;
            }
            if (best != null && gui.m_playerGrid != null) gui.m_playerGrid.SetGamepadSelection(best.m_gridPos);
        }

        // In place of the game's chest update: the chest panel up, showing the object's slots.
        public static void ShowPanel(InventoryGui gui, Player player)
        {
            if (!gui.m_container.gameObject.activeSelf) gui.m_container.gameObject.SetActive(true);
            Buttons(gui, false);   // a mannequin, stand or altar has no stack-all or take-all
            Refresh();
            // the weight box, as on a chest: the weight of what is on the object (nothing stays in a catapult or an altar: 0).
            // The game only fills it for a real chest, so without this it showed whatever the last chest weighed.
            if (gui.m_containerWeight != null)
            {
                string w = Mathf.CeilToInt(Inv.GetTotalWeight()).ToString();
                if (gui.m_containerWeight.text != w) gui.m_containerWeight.text = w;
            }
            gui.ContainerGrid.UpdateInventory(Inv, player, F_dragItem.GetValue(gui) as ItemDrop.ItemData);
            if (first) { first = false; gui.ContainerGrid.ResetView(); }
            if (gui.m_containerName != null)
            {
                string t = Inv.GetName();
                if (bowl != null && bowl.m_bossItem != null)
                {
                    // the altar's offering, and how many the bag has of how many it needs
                    int have = player.GetInventory().CountItems(bowl.m_bossItem.m_itemData.m_shared.m_name);
                    t += "   " + Localize(bowl.m_bossItem.m_itemData.m_shared.m_name) + " " + have + "/" + bowl.m_bossItems;
                }
                if (gui.m_containerName.text != t) gui.m_containerName.text = t;
            }
        }

        // The stand-ins are rebuilt only when what is on the object changes.
        static void Refresh()
        {
            int n = Count();
            int[] now = new int[n];
            for (int i = 0; i < n; i++) now[i] = armor != null ? (armor.HaveAttachment(places[i]) ? armor.GetAttachedItem(places[i]) : 0) : stand != null && stand.HaveAttachment() ? stand.GetAttachedItem() : 0;
            bool same = now.Length == shown.Length;
            for (int i = 0; same && i < n; i++) same = now[i] == shown[i];
            if (same) return;
            shown = now;
            Inv.RemoveAll();
            for (int i = 0; i < n; i++)
            {
                if (now[i] == 0 || ObjectDB.instance == null) continue;
                GameObject prefab = ObjectDB.instance.GetItemPrefab(now[i]);
                ItemDrop d = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (d == null) continue;
                ItemDrop.ItemData copy = d.m_itemData.Clone();
                copy.m_stack = 1; copy.m_equipped = false; copy.m_dropPrefab = prefab;
                Inv.AddItem(copy, new Vector2i(i % width, i / width));
            }
            whyFrame = -1;
        }

        // An item that cannot go on the open object right now is greyed in the bag. Only what this greyed is put back.
        static readonly HashSet<InventoryElement> greyed = new HashSet<InventoryElement>();
        public static void Grey(InventoryElement el, ItemDrop.ItemData it)
        {
            if (el.m_icon == null) return;
            bool g = Active && it != null && Why(it) != null;
            if (g) { if (el.m_icon.color != Greyed) el.m_icon.color = Greyed; greyed.Add(el); }
            else if (greyed.Remove(el)) el.m_icon.color = Color.white;
        }
    }

    // ---------- the inventory flow ----------
    static class Core
    {
        public static bool InGuiUpdate, InGridUpdate, SuppressY, SuppressB, SuppressA, SuppressAX, SuppressX, SuppressTriggers, SuppressGridAll, StripShowing;

        // favorites in the world (set by Favorites.WorldUpdate)
        public static bool FavWorld, DownOwned, DownIsGP;
        // FavLT: LT is the favorites modifier right now, so its own vanilla jobs are off.
        // FavFace: LT is held, so the face buttons' vanilla jobs are off too.
        public static bool FavLT, FavFace;
        // a radial group opened with LT + D-pad: LT stays away from the radial until it is let go, so letting go of it does
        // not pick or close (the game's release-to-use reads LT's release)
        public static bool PadRadialLt;
        public static bool TakenByFavorites(string name)
        {
            if (name == "JoyRadial" || name == "JoyHide") return true;
            return FavFace && (name == "JoyJump" || name == "JoyUse" || name == "JoyDodge" || name == "JoyBuildMenu" || name == "JoyInventory");
        }
        static int injectFrame = -10;
        public static void Inject() { injectFrame = Time.frameCount; }
        // Lets one vanilla D-pad down press through, after a hold.
        public static bool TakeInject()
        {
            if (Time.frameCount - injectFrame > 1) return false;
            injectFrame = -10;
            return true;
        }

        // picking a D-pad direction for a favorite (LT + X)
        public static bool Assigning;
        public static bool QuickAssign;   // the Controller Hotbar window was opened with LT + Start: closing it closes the bag too
        public static int QuickFrame = -10;   // the frame it opened on: the Start that opened it does not also close it
        static ItemDrop.ItemData assignItem;
        static bool wasOn;
        static float closedAt = -10f;
        const float CloseGrace = 0.5f;

        static readonly FieldInfo F_dragGo = AccessTools.Field(typeof(InventoryGui), "m_dragGo");
        static readonly FieldInfo F_dragItem = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        static readonly FieldInfo F_dragAmount = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
        static bool onSource, noSlot;
        static bool yTracking, yFired;
        static float yStart;
        static readonly FieldInfo F_splitItem = AccessTools.Field(typeof(InventoryGui), "m_splitItem");
        static readonly FieldInfo F_splitInventory = AccessTools.Field(typeof(InventoryGui), "m_splitInventory");
        static readonly MethodInfo M_SetupDragItem = AccessTools.Method(typeof(InventoryGui), "SetupDragItem");
        static readonly MethodInfo M_ShowSplitDialog = AccessTools.Method(typeof(InventoryGui), "ShowSplitDialog");
        static readonly MethodInfo M_HideSplitDialog = AccessTools.Method(typeof(InventoryGui), "HideSplitDialog");
        static readonly MethodInfo M_OnSelectedItem = AccessTools.Method(typeof(InventoryGui), "OnSelectedItem");

        // frame state, worked out before vanilla runs
        static bool active, split, subDialog, dragging, lt;
        static InventoryGrid grid;
        static ItemDrop.ItemData item;

        // tap / hold: a button with a hold fires its tap on release
        const int None = 0, BtnY = 1, BtnX = 2, BtnA = 3, BtnLS = 4;
        static Inventory holdInv; static bool holdBag;   // what a sort hold will sort, fixed when the hold starts
        static bool aArmed;
        static int holdBtn;
        static float holdStart, holdDur;
        static bool holdFired;
        static ItemDrop.ItemData holdItem;
        static InventoryGrid holdGrid;
        static bool yArmed, bArmed, xArmed;

        // split slider
        static int stickDir;
        static float stickT0, stickAcc, dpadNext;
        static int dpadDir;

        static bool failed;
        public static void Fail(Exception e)
        {
            InGuiUpdate = InGridUpdate = SuppressY = SuppressB = SuppressA = SuppressAX = SuppressTriggers = SuppressGridAll = FavWorld = FavLT = FavFace = Assigning = false;
            if (!failed) { failed = true; Plugin.Log.LogError("Controller Support Plus stopped a frame early: " + e); }
        }

        // The mod's own button reads go through these, so they are never hidden by its own suppression.
        public static bool Bypass;
        public static int AssignEndFrame = -1;
        public static bool RawDown(string name) { Bypass = true; bool r = ZInput.GetButtonDown(name); Bypass = false; return r; }
        public static bool RawHeld(string name) { Bypass = true; bool r = ZInput.GetButton(name); Bypass = false; return r; }

        static bool Open(GameObject go) { return go != null && go.activeSelf; }
        static bool Open(Component c) { return c != null && c.gameObject.activeSelf; }

        public static void BeginFrame(InventoryGui gui)
        {
            SuppressY = SuppressB = SuppressA = SuppressAX = SuppressX = SuppressTriggers = SuppressGridAll = false;
            HoldMark.Progress = 0f; HoldMark.Grid = null;
            grid = null; item = null; split = subDialog = dragging = lt = onSource = noSlot = false;
            active = Plugin.Enabled.Value && Player.m_localPlayer != null && InventoryGui.IsVisible() && ZInput.IsExclusiveGamepadActive();
            if (!active) { StopAssign(false); QuickAssign = false; Favorites.QuickCover(false); Cursor.Stop(); return; }

            subDialog = Open(gui.m_trophiesPanel) || Open(gui.m_achievementsPanel) || Open(gui.m_skillsDialog)
                     || Open(gui.m_textsDialog) || Open(gui.m_variantDialog);
            if (subDialog) { Cursor.Stop(); return; }
            SuppressY = true;   // on the controller Y never closes the inventory: it is the chest's button, and B closes
            split = gui.m_splitDialog != null && gui.m_splitDialog.IsActive;
            if (split) { SuppressY = true; Cursor.Pause(); return; }
            // LT + Start in play opened the bag for the Controller Hotbar window: it opens at once, nothing in hand
            if (QuickAssign && !Assigning) { Assigning = true; assignItem = null; Favorites.BeginEditEmpty(); }
            if (Assigning && assignItem == null) { SuppressY = SuppressB = SuppressA = SuppressAX = SuppressX = SuppressTriggers = SuppressGridAll = true; Cursor.Pause(); return; }
            // Start opens the Controller Support Plus Settings window (the game's own menu only opens with the bag closed)
            if (ModSettings.Frame(gui, !Assigning && (F_dragGo.GetValue(gui) as GameObject) == null)) { Cursor.Pause(); return; }

            // cursor mode: the left stick steers a cursor, and whatever slot it is over becomes the focus
            if (Assigning) Cursor.Pause(); else Cursor.Step(gui);
            dragging = (F_dragGo.GetValue(gui) as GameObject) != null;
            if (Cursor.On && Cursor.OffGrid && !Assigning)
            {
                // off the grid: the slots ignore input; A is the cursor's click or, outside the panels, a drop
                SuppressY = SuppressGridAll = true;
                SuppressB = dragging;   // B cancels a carry, otherwise it closes as usual
                if (dragging || Cursor.PanelGrid == null) return;
                // Over the bag or the chest but not on a slot: everything carries on as if on a slot with nothing in it.
                // The guide keeps the same labels, and the chest's own actions (fill stacks, take all, sort) still work.
                grid = Cursor.PanelGrid; item = null; noSlot = true; lt = ZInput.GetButton("JoyLTrigger");
                return;
            }

            InventoryGrid cg = gui.ContainerGrid;
            if (gui.m_playerGrid != null && gui.m_playerGrid.m_uiGroup != null && gui.m_playerGrid.m_uiGroup.IsActive) grid = gui.m_playerGrid;
            else if (gui.IsContainerOpen() && cg != null && cg.m_uiGroup != null && cg.m_uiGroup.IsActive) grid = cg;
            if (grid == null) return;

            item = grid.GetGamepadSelectedItem();
            onSource = dragging && item != null && item == (F_dragItem.GetValue(gui) as ItemDrop.ItemData); // focus is still on the stack being lifted from
            lt = ZInput.GetButton("JoyLTrigger");
            bool replace = Plugin.ReplaceVanillaTriggers.Value;

            if (Assigning)
            {
                if (grid != gui.m_playerGrid || item == null || item != assignItem || dragging) StopAssign(false);
                else { SuppressY = SuppressB = SuppressGridAll = true; return; } // the grid ignores everything until a direction is picked
            }

            SuppressY = true;                                 // Y transfers, it no longer closes the inventory from a grid
            SuppressTriggers = replace;                        // vanilla LT+A / RT+A / LT+X off
            SuppressAX = replace && lt && (!dragging || onSource); // LT layer owns A and X (while carrying, only on the source stack)
            SuppressA = !dragging && !lt && item != null && item.m_stack > 1; // on a stack, A is tap to move / hold to split, so it fires on release
            SuppressB = dragging;                              // B closes, always. It only means something else while carrying (cancel)
            // X on a bag item: tap equips or uses, hold opens the favorite overlay. Having a hold, it fires on release.
            SuppressX = !dragging && !lt && Plugin.FavEnabled.Value && !Favorites.TopRow && grid == gui.m_playerGrid && item != null;        }

        // Leaves the assign overlay. Anything not saved is put back.
        static void StopAssign(bool save)
        {
            if (!Assigning) return;
            Assigning = false;
            AssignEndFrame = Time.frameCount;
            Favorites.FinishEdit();   // there is no Save: every change counts as it is made, and closing keeps them
        }

        public static void Process(InventoryGui gui)
        {
            if (!Assigning) Favorites.HidePicker();
            Deposit.Frame(gui, active && !subDialog);
            if (ModSettings.Blocking) { holdBtn = None; yArmed = bArmed = aArmed = xArmed = false; return; }   // the settings window has the buttons
            // the Controller Hotbar window opened with LT + Start: it has the buttons; closing it (B or Start) closes the bag
            if (Assigning && assignItem == null && active && !subDialog)
            {
                holdBtn = None; yArmed = bArmed = aArmed = xArmed = false;
                Favorites.PickInput(RawDown("JoyButtonA"));
                if (Time.frameCount > QuickFrame + 1 && (RawDown("JoyButtonB") || RawDown("JoyMenu"))) StopAssign(true);
                UITooltip.HideTooltip();
                if (!Assigning)
                {
                    Favorites.HidePicker(); Favorites.QuickCover(false); Hints.Hide();
                    if (QuickAssign) { QuickAssign = false; gui.Hide(); }
                    return;
                }
                Favorites.ShowPicker(gui, null); Favorites.QuickCover(QuickAssign); Shots.Want("assign-overlay");
                Hints.Show(gui, gui.IsContainerOpen());
                Hints.Cell(0, Favorites.PickCanA(), Favorites.PickCarrying ? "Place" : "Grab", null, false, 0f);
                Hints.Cell(1, false, null, null, false, 0f);
                Hints.Cell(2, false, null, null, false, 0f);
                Hints.Cell(3, true, "Close", null, false, 0f);
                Hints.Extra("LT", false, null);
                return;
            }
            if (!active || subDialog)
            {
                holdBtn = None; yArmed = bArmed = aArmed = xArmed = false;
                // Closing: the game fades the inventory out over a moment. Everything of the mod's stays exactly as it
                // was for that moment and fades out with it. Taking it all away at once showed, for a split second,
                // the inventory jumping back to its vanilla place with the dimming and the guide gone.
                bool closed = !subDialog && !InventoryGui.IsVisible();
                if (wasOn && closed) closedAt = Time.unscaledTime;
                wasOn = false;
                if (closed && Time.unscaledTime - closedAt < CloseGrace) return;
                Hints.Hide();
                Dim.Clear();
                CraftHints.Hide();
                ChestButtons.Apply(gui, false, 0f);
                return;
            }
            wasOn = true;
            Dim.Apply(gui);
            // The button guide under the bag is about items. In crafting and in the misc panel it says nothing useful,
            // so it goes away there; crafting gets a short list of its own beside the panel.
            int panelNow = Dim.Current;
            bool sidePanel = (panelNow == 2 || panelNow == 3) && !dragging && !split && !Assigning;
            CraftHints.Show(gui, sidePanel && panelNow == 2);
            if (!split && !Assigning) Scroll.Tick(gui);
            float now = Time.unscaledTime;
            bool yHeld = ZInput.GetButton("JoyButtonY"), bHeld = ZInput.GetButton("JoyButtonB"), aHeld = ZInput.GetButton("JoyButtonA");
            bool yDown = yArmed && ZInput.GetButtonDown("JoyButtonY");
            bool bDown = bArmed && ZInput.GetButtonDown("JoyButtonB");
            bool aDown = RawDown("JoyButtonA");
            bool aFresh = aArmed && aDown;
            bool xHeld = RawHeld("JoyButtonX");
            bool xFresh = xArmed && RawDown("JoyButtonX");
            if (!xHeld) xArmed = true;   // ignore the press that opened the chest
            Cursor.FollowDrag(gui);
            if (!yHeld) yArmed = true;   // ignore the press that opened the inventory
            if (!bHeld) bArmed = true;
            if (!aHeld) aArmed = true;
            bool chest = gui.IsContainerOpen();

            // --- Y: the chest's button, wherever the focus or the cursor is ---
            // Tap fills the chest's stacks from the bag; hold takes everything out. It does not depend on which panel
            // or slot is under the cursor: it is about the chest, and the chest is open or it is not.
            float yRing = 0f;
            bool yCan = chest && !split && !Assigning && !dragging && !Deposit.Active;   // a mannequin, stand or altar has no stack-all or take-all
            if (!yCan) yTracking = false;
            else
            {
                if (yDown) { yTracking = true; yFired = false; yStart = now; }
                if (yTracking)
                {
                    float held = now - yStart, dur = Mathf.Max(0.05f, Plugin.HoldShort.Value);
                    if (!yFired && held >= dur && (!yHeld || held >= dur + Plugin.RingFullBeat.Value))
                    {
                        yFired = true;
                        if (gui.m_takeAllButton != null) gui.m_takeAllButton.onClick.Invoke();
                    }
                    else if (!yHeld && !yFired) { if (gui.m_stackAllButton != null) gui.m_stackAllButton.onClick.Invoke(); }
                    else if (!yFired)
                    {
                        float d = Mathf.Min(Plugin.RingDelay.Value, dur * 0.8f);
                        yRing = held < d ? 0f : Mathf.Clamp01((held - d) / (dur - d));
                    }
                    if (!yHeld) yTracking = false;
                }
            }
            Hints.SetY(yCan, yRing);
            ChestButtons.Apply(gui, true, yRing);

            if (split)
            {
                holdBtn = None;
                ProcessSplit(gui, now, chest && RawDown("JoyRTrigger"));
                Shots.Want("split");
                Hints.Show(gui, chest);
                Hints.Cell(0, true, "Carry", null, false, 0f);
                Hints.Cell(1, false, null, null, false, 0f);
                Hints.Cell(2, false, null, null, false, 0f);
                Hints.Cell(5, chest, "Transfer", null, false, 0f);
                Hints.Cell(3, true, "Cancel", null, false, 0f);
                Hints.Extra("LS", true, "Amount");
                return;
            }
            if (Cursor.On && Cursor.OffGrid && !Assigning && !noSlot)
            {
                holdBtn = None;
                Shots.Want(dragging ? "cursor-carrying" : "cursor");
                if (sidePanel)
                {
                    if (aFresh) Cursor.Click(gui);
                    Hints.Hide();
                    StripShowing = Plugin.ShowHints.Value;   // still counts as showing, so the game's own inventory hints stay away
                    return;
                }
                Hints.Show(gui, chest);
                if (dragging)
                {
                    // carrying something out of the inventory and pressing A drops it, as dragging it out with a mouse does
                    if (aFresh && Cursor.Outside) Cursor.Drop(gui);
                    Hints.Cell(0, Cursor.Outside, "Drop", null, false, 0f);
                    Hints.Cell(3, true, "Cancel", null, false, 0f);
                }
                else
                {
                    if (aFresh) Cursor.Click(gui);
                    Hints.Cell(0, Cursor.OverButton, "Select", null, false, 0f);
                    Hints.Cell(3, true, "Close", null, false, 0f);
                }
                Hints.Cell(1, false, null, null, false, 0f);
                Hints.Cell(2, false, null, null, false, 0f);
                Hints.Extra("LT", false, null);
                return;
            }
            if (grid == null) { holdBtn = None; Hints.Hide(); StripShowing = Plugin.ShowHints.Value; return; }

            bool bag = grid == gui.m_playerGrid;
            Inventory inv = grid.GetInventory();
            int stack = item != null ? item.m_stack : 0;

            if (Assigning)
            {
                holdBtn = None;
                // The overlay works like a small inventory. The D-pad or the stick moves a focus over the squares, A
                // places the item on the focused square or picks it back up, and B closes. Every change counts as it is made.
                Favorites.PickInput(aFresh);
                if (bDown) StopAssign(true);   // B closes; there is nothing to confirm
                UITooltip.HideTooltip();   // the bag item's info panel would show behind the window
                if (Assigning) { Favorites.ShowPicker(gui, item); Shots.Want("assign-overlay"); }
                Hints.Show(gui, chest);
                Hints.Cell(0, Favorites.PickCanA(), Favorites.PickCarrying ? "Place" : "Grab", null, false, 0f);
                Hints.Cell(1, false, null, null, false, 0f);
                Hints.Cell(2, false, null, null, false, 0f);
                Hints.Cell(3, true, "Close", null, false, 0f);
                Hints.Extra("LT", false, null);
                return;
            }

            if (dragging)
            {
                // vanilla does the work here: A places, B cancels
                holdBtn = None;
                // LT + A again on the same stack lifts one more, so an amount can be built up by hand
                Shots.Want("carrying");
                bool more = SuppressAX && onSource;
                int carried = (int)F_dragAmount.GetValue(gui);
                if (more && aDown && carried < stack) { F_dragAmount.SetValue(gui, carried + 1); Fx(gui); }
                Hints.Show(gui, chest);
                Hints.Cell(0, !more || carried < stack, more ? "Take 1" : "Place", null, false, 0f);
                Hints.Cell(1, false, null, null, false, 0f);
                Hints.Cell(2, false, null, null, false, 0f);
                Hints.Cell(3, true, "Cancel", null, false, 0f);
                Hints.Extra("LT", false, null);
                return;
            }

            // A greyed bag item while a mannequin, stand or altar is open: locked. No button does anything to it, and the
            // guide is all dimmed, with RT saying why (Won't fit, Slot taken, Need 2).
            if (bag && Deposit.Locked(item))
            {
                holdBtn = None;
                Shots.Want("object-locked");
                Hints.Show(gui, chest);
                Hints.Cell(0, false, "Move", null, false, 0f);
                Hints.Cell(1, false, null, null, false, 0f);
                Hints.Cell(2, false, null, null, false, 0f);
                Hints.Cell(5, false, Deposit.Why(item), null, false, 0f);
                Hints.Cell(6, false, "Sort", null, false, 0f);
                Hints.Cell(3, true, "Close", null, false, 0f);
                Hints.Extra("LT", false, null);
                return;
            }

            // --- presses ---
            // RT sends the focused item away. With a chest open it goes across to the other container; with only the
            // bag open there is nowhere to send it, so it is dropped. LT turns the chest's transfer into a drop.
            if (RawDown("JoyRTrigger") && item != null && Deposit.Active && bag && !lt) { holdBtn = None; Deposit.Put(item); }   // onto the open mannequin, stand or altar, at once
            /* LT + RT with a chest open: across to the other side, all but one (0.88.6; it dropped the item before) */
            else if (RawDown("JoyRTrigger") && item != null && chest && lt && !Deposit.Active)
            {
                holdBtn = None;
                InventoryGrid to = bag ? gui.ContainerGrid : gui.m_playerGrid;
                Dictionary<Vector2i, int> before = Landed.Snapshot(to);
                int moved = stack > 1 ? AllButOne.Move(inv, to.GetInventory(), item, stack - 1) : 0;
                if (moved > 0) Landed.Mark(to, before);
            }
            else if (RawDown("JoyRTrigger") && item != null)
            {
                holdBtn = None;
                bool across = chest && !lt;
                InventoryGrid to = across ? (bag ? gui.ContainerGrid : gui.m_playerGrid) : null;
                Dictionary<Vector2i, int> before = across ? Landed.Snapshot(to) : null;
                Select(gui, grid, item, across ? InventoryGrid.Modifier.Move : InventoryGrid.Modifier.Drop);
                if (across) Landed.Mark(to, before);   // the slots it went into flash white, so it can be found
            }
            // Holding the left stick's click sorts whichever of the two the focus is in, the bag or the chest. Sorting
            // rearranges a layout the player may have built by hand, so it takes a deliberate hold; a tap does nothing.
            else if (RawDown("JoyLStick")) { StartHold(BtnLS, now, Plugin.HoldLong.Value); holdInv = inv; holdBag = bag; }
            else if (lt)
            {
                if (holdBtn != BtnLS) holdBtn = None;   // a sort hold carries on with LT down
                if (aDown && stack > 1) M_SetupDragItem.Invoke(gui, new object[] { item, inv, 1 });               // take 1
                else if (Plugin.FavEnabled.Value && !Favorites.TopRow && bag && item != null && ZInput.GetButtonDown("JoyButtonX")) { Assigning = true; assignItem = item; Favorites.BeginEdit(item); } // favorite
            }
            else
            {
                // A on a stack: tap moves, hold splits. (On a single item vanilla's A still picks it up at once.)
                if (aFresh && stack > 1) StartHold(BtnA, now, Plugin.HoldShort.Value);
                // X on a bag item: tap equips or uses it, hold opens the favorite overlay. (LT + X still opens it at once.)
                else if (xFresh && SuppressX) StartHold(BtnX, now, Plugin.HoldShort.Value);
            }
            // --- hold in progress ---
            float ring = 0f;
            if (holdBtn != None)
            {
                bool held = holdBtn == BtnY ? yHeld : holdBtn == BtnX ? xHeld : holdBtn == BtnLS ? RawHeld("JoyLStick") : aHeld;
                // focus moved: cancel quietly. Not for the sort: clicking the stick nudges the cursor, and the sort
                // is about the whole container, not the slot under it.
                if (holdBtn != BtnLS && (holdGrid != grid || holdItem != item)) holdBtn = None;
                // The ring is full at holdDur. The action waits one short beat after that, so the full ring is actually
                // seen before the screen changes. Letting go during that beat still counts as a completed hold.
                else if (!holdFired && now - holdStart >= holdDur && (!held || now - holdStart >= holdDur + Plugin.RingFullBeat.Value))
                {
                    holdFired = true;
                    if (holdBtn == BtnA) M_ShowSplitDialog.Invoke(gui, new object[] { item, inv });
                    else if (holdBtn == BtnX && !Favorites.TopRow) { Assigning = true; assignItem = item; Favorites.BeginEdit(item); }
                    else if (holdBtn == BtnLS && holdInv != null) { Sorter.Run(holdInv, holdBag); Favorites.EquipSound(); }   // the equip sound marks the sort
                    if (!held) holdBtn = None;
                }
                else if (!held)
                {
                    if (!holdFired)
                    {
                        if (holdBtn == BtnA) Select(gui, grid, item, InventoryGrid.Modifier.Select);   // pick the stack up
                        else if (holdBtn == BtnX) { if (grid.m_onRightClick != null) grid.m_onRightClick(grid, item, item.m_gridPos); }   // the game's own equip / use
                    }
                    holdBtn = None;
                }
                else if (!holdFired)
                {
                    float t = now - holdStart, d = Mathf.Min(Plugin.RingDelay.Value, holdDur * 0.8f);
                    ring = t < d ? 0f : Mathf.Clamp01((t - d) / (holdDur - d));
                }
            }

            Shots.Want(ring > 0.45f ? "hold-ring" : lt ? "lt-layer" : chest ? "inventory-chest" : "inventory");
            HoldMark.Progress = Mathf.Max(ring, yRing); HoldMark.Grid = grid;   // shown on the cursor, or on the slot with the D-pad

            // --- hint strip ---
            Hints.Show(gui, chest);
            // RT keeps one label for the whole screen: Transfer while a chest is open, Drop when it is not. It dims when
            // there is no item under the focus, and only LT changes the word.
            if (Deposit.Active && bag && !lt)
            {
                // with an object open, RT puts the item on it. When it cannot, the dimmed label says why: Won't fit, Slot taken, Need 2
                string why = item != null ? Deposit.Why(item) : null;
                Hints.Cell(5, item != null && why == null, why ?? Deposit.PutLabel(), null, false, 0f);
            }
            else if (Deposit.Active && !lt) Hints.Cell(5, item != null, "Take", null, false, 0f);   // on the object's slots: RT takes the item back off
            else if (chest && lt && !Deposit.Active) Hints.Cell(5, item != null && stack > 1, "All but 1", null, false, 0f);   /* a single item has no "all but one" */
            else Hints.Cell(5, item != null, chest && !lt ? "Transfer" : "Drop", null, false, 0f);
            Hints.Cell(6, true, "Sort", "Hold", true, holdBtn == BtnLS ? ring : 0f);
            if (lt)
            {
                Hints.Cell(0, stack > 1, "Take 1", null, false, 0f);
                // LT + X opens the Controller Hotbar window; with Top row assignment there is no window, so X shows its usual label, dimmed
                if (Favorites.TopRow) Hints.Cell(1, false, null, null, false, 0f);
                else Hints.Cell(1, Plugin.FavEnabled.Value && bag && item != null, "Assign", null, false, 0f);
                Hints.Cell(2, false, null, null, false, 0f);
                Hints.Cell(3, true, "Close", null, false, 0f);   // LT + B closes, as B does ("All but 1" was removed in 0.29.0)
                Hints.Extra("LT", true, "Held");
                return;
            }
            bool canSplit = stack > 1;
            Hints.Cell(0, item != null, "Move", canSplit ? "Hold \u00b7 Split" : null, canSplit, holdBtn == BtnA ? ring : 0f);
            string use = null;
            if (item != null)
            {
                // equipping is the bag's alone; a consumable is used from a chest too (X uses it there, the label was greyed
                // until 0.82.12)
                if (bag && item.IsEquipable()) use = Player.m_localPlayer.IsItemEquiped(item) ? "Unequip" : "Equip";
                else if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable) use = "Use";
            }
            bool canFav = Plugin.FavEnabled.Value && !Favorites.TopRow && bag && item != null;   // top row mode: no window, X is the game's own
            if (canFav && use != null) Hints.Cell(1, true, use, "Hold \u00b7 Assign", true, holdBtn == BtnX ? ring : 0f);
            else if (canFav) Hints.Cell(1, false, null, "Hold \u00b7 Assign", true, holdBtn == BtnX ? ring : 0f);   // nothing to equip: the usual label stays, dimmed, and the hold is lit
            else Hints.Cell(1, use != null, use, null, false, 0f);
            // Y is the chest's button. With no chest open it has nothing to do, and shows its usual label dimmed.
            Hints.Cell(3, true, "Close", null, false, 0f);
            // LT is lit whenever its layer has something to offer on this item, and holds steady otherwise
            Hints.Extra("LT", item != null && (canSplit || bag), "More");
        }
        static void StartHold(int btn, float now, float dur)
        {
            holdBtn = btn; holdStart = now; holdDur = Mathf.Max(0.05f, dur); holdFired = false; holdItem = item; holdGrid = grid;
        }

        // Runs vanilla's own handler, so unequip, sounds and rules stay exactly vanilla.
        static void Select(InventoryGui gui, InventoryGrid g, ItemDrop.ItemData it, InventoryGrid.Modifier mod)
        {
            M_OnSelectedItem.Invoke(gui, new object[] { g, it, it.m_gridPos, mod });
        }

        static Inventory Other(InventoryGui gui, Inventory from)
        {
            Inventory bagInv = Player.m_localPlayer.GetInventory();
            return from == bagInv ? gui.ContainerGrid.GetInventory() : bagInv;
        }

        static void Fx(InventoryGui gui)
        {
            gui.m_moveItemEffects.Create(gui.transform.position, Quaternion.identity, null, 1f, -1, default(ZDOID));
        }

        static bool MovePartial(InventoryGui gui, Inventory from, Inventory to, ItemDrop.ItemData it, int amount)
        {
            if (from == null || to == null || it == null || amount <= 0 || it.m_shared.m_questItem) return false;
            if (!from.ContainsItem(it)) return false;
            if (Deposit.Moving(from, to, it)) return true;   // a mannequin, stand or altar is open: its own put-on or take-off
            if (amount >= it.m_stack)
            {
                // the whole stack: unequipped first, as the game does before it moves an item (equipped arrows would
                // otherwise land in the chest still marked as equipped)
                Player pl = Player.m_localPlayer;
                if (pl != null && from == pl.GetInventory() && pl.IsItemEquiped(it)) { pl.RemoveEquipAction(it); pl.UnequipItem(it, false); }
                to.MoveItemToThis(from, it);
                return true;
            }
            ItemDrop.ItemData part = it.Clone();
            part.m_stack = amount;
            part.m_equipped = false;
            if (!to.CanAddItem(part, amount) || !to.AddItem(part))
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$inventory_full", 0, null, false);
                return false;
            }
            from.RemoveItem(it, amount);
            return true;
        }

        // Split slider: holding the stick accelerates on top of vanilla's one-at-a-time stepping,
        // D-pad up / down step by a fixed amount, Y sends the amount straight to the other container.
        static void ProcessSplit(InventoryGui gui, float now, bool send)
        {
            Slider s = gui.m_splitDialog.m_splitSlider;
            if (s == null) return;

            float lx = ZInput.GetJoyLeftStickX();
            if (Mathf.Abs(lx) < 0.25f) lx = 0f;
            if (ZInput.GetButton("JoyDPadLeft")) lx = -1f; else if (ZInput.GetButton("JoyDPadRight")) lx = 1f;
            int dir = lx < 0f ? -1 : lx > 0f ? 1 : 0;
            if (dir != stickDir) { stickDir = dir; stickT0 = now; stickAcc = 0f; }
            else if (dir != 0 && now - stickT0 > 0.25f)
            {
                float ramp = Mathf.Clamp01((now - stickT0 - 0.25f) / 0.9f);
                stickAcc += Mathf.Abs(lx) * Mathf.Lerp(Plugin.SliderStartSpeed.Value, Plugin.SliderMaxSpeed.Value, ramp) * Time.unscaledDeltaTime;
                int k = Mathf.FloorToInt(stickAcc);
                if (k > 0) { stickAcc -= k; s.value = Mathf.Clamp(s.value + dir * k, s.minValue, s.maxValue); }
            }

            int ud = ZInput.GetButton("JoyDPadUp") ? 1 : ZInput.GetButton("JoyDPadDown") ? -1 : 0;
            if (ud != 0 && (ud != dpadDir || now >= dpadNext))
            {
                dpadNext = now + (ud != dpadDir ? 0.28f : 0.11f);
                s.value = Mathf.Clamp(s.value + ud * Plugin.DpadStep.Value, s.minValue, s.maxValue);
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(s.gameObject); // keep focus on the slider
            }
            dpadDir = ud;

            if (send)
            {
                ItemDrop.ItemData it = F_splitItem.GetValue(gui) as ItemDrop.ItemData;
                Inventory from = F_splitInventory.GetValue(gui) as Inventory;
                if (it != null && from != null && MovePartial(gui, from, Other(gui, from), it, Mathf.RoundToInt(s.value))) Fx(gui);
                M_HideSplitDialog.Invoke(gui, null);
                F_splitItem.SetValue(gui, null);
                F_splitInventory.SetValue(gui, null);
            }
        }
    }

    // ---------- hint strip ----------
    // Five fixed slots (A X Y B + one tag) docked under the lowest open panel. Slots never move;
    // an unavailable action dims its glyph and drops its label.
    static class Hints
    {
        class CellUI { public CanvasGroup glyph; public Image fill, track, dpadIcon; public TMP_Text label, sub, key; public string keyName; public RectTransform box; }
        // slots 0-3 are the face buttons; 4 is LT (the modifier); 5 is RT and 6 is the left stick's click
        static readonly string[] KeyName = { "JoyButtonA", "JoyButtonX", "JoyButtonY", "JoyButtonB", "JoyLTrigger", "JoyRTrigger", "JoyLStick" };
        const float LabelX = 44f;   // where a label starts inside its slot: 4 clear of the hold ring, about 11 from a plain button
        const float Lift = 2f;   // the wood art is heavier along its bottom edge, so the row sits slightly above the rectangle's centre

        // Button glyphs are the game's own (the black and white ones in vanilla hints), matched to the connected controller.
        static readonly System.Collections.Generic.Dictionary<string, string> glyphs = new System.Collections.Generic.Dictionary<string, string>();
        static float glyphsUntil;
        public static string Glyph(string button)
        {
            if (Time.unscaledTime > glyphsUntil) { glyphs.Clear(); glyphsUntil = Time.unscaledTime + 1f; } // re-read every second in case the controller changes
            string g;
            if (!glyphs.TryGetValue(button, out g))
            {
                g = ZInput.instance != null ? ZInput.instance.GetBoundKeyString(button, true) : "";
                glyphs[button] = g;
            }
            return g;
        }

        public static string DpadGlyph()
        {
            string up = Glyph("JoyDPadUp");
            int at = up.IndexOf("name=\"");
            return at > 0 ? up.Substring(0, at) + "name=\"dpad\">" : "";
        }

        static void SetKey(CellUI c, string text)
        {
            if (c.key.text != text) c.key.text = text;
            float size = Plugin.GlyphSize.Value;
            if (c.key.fontSize != size) c.key.fontSize = size;
        }
        // overX / overY: how far the panel's art reaches past the panel's own edges, so the strip can line up with what is drawn
        class Strip { public bool homeAnchors; public Vector2 anchorMin, anchorMax; public Transform home; public GameObject root; public RectTransform rt; public CellUI[] cells = new CellUI[7]; public float overX, overY; public bool laid; public float[] widest = new float[7]; public float widestDrop; }

        static Strip bagStrip, chestStrip, current;
        static Sprite circle, ringSprite, box, dpad;
        public static Sprite Dpad() { if (dpad == null) MakeSprites(); return dpad; }

        public static void Hide()
        {
            Core.StripShowing = false;
            if (bagStrip != null && bagStrip.root != null) bagStrip.root.SetActive(false);
            if (chestStrip != null && chestStrip.root != null) chestStrip.root.SetActive(false);
            current = null;
        }

        public static void Show(InventoryGui gui, bool chest)
        {
            if (!Plugin.ShowHints.Value) { Hide(); return; }
            if (bagStrip == null || bagStrip.root == null) bagStrip = SafeBuild(gui, gui.m_player);
            if (chestStrip == null || chestStrip.root == null) chestStrip = SafeBuild(gui, gui.m_container);
            current = chest ? chestStrip : bagStrip;
            Strip other = chest ? bagStrip : chestStrip;
            if (other != null && other.root != null) other.root.SetActive(false);
            if (current == null || current.root == null) { Core.StripShowing = false; return; }
            current.root.SetActive(true);
            if (!current.laid) Layout(current);
            // With the Controller Hotbar window open the guide sits under it, as it sits under the bag: lined up with its left
            // edge, the same gap below (until 0.75.1 it stayed under the bag, behind the window; in 0.75.1 it sat inside it).
            // Back under its panel the moment the window closes.
            if (current.home == null) current.home = current.root.transform.parent;
            RectTransform win = Core.Assigning ? Favorites.PickPanel : null;
            Transform want = win != null ? (Transform)win : current.home;
            if (current.root.transform.parent != want) current.root.transform.SetParent(want, false);
            if (win != null)
            {
                float s2 = Mathf.Max(0.5f, Plugin.HintScale.Value);
                current.rt.localScale = new Vector3(s2, s2, 1f);
                current.rt.anchorMin = current.rt.anchorMax = new Vector2(0f, 0f);   // the window's bottom left corner
                current.rt.pivot = new Vector2(0f, 1f);
                current.rt.sizeDelta = new Vector2(lastWidth, 34f + 2f * Mathf.Max(0f, Plugin.HintPadding.Value));
                float inset2 = Mathf.Max(0f, Plugin.HintPadding.Value) + 0.65f - Lift;
                current.rt.anchoredPosition = new Vector2(0f, -(Plugin.HintOffsetY.Value - inset2));
                yShown = false;
                Core.StripShowing = true;
                Cell(5, false, chest ? "Transfer" : null, null, false, 0f);
                Cell(6, false, null, null, false, 0f);
                return;
            }
            if (current.homeAnchors) { current.rt.anchorMin = current.anchorMin; current.rt.anchorMax = current.anchorMax; }
            else { current.anchorMin = current.rt.anchorMin; current.anchorMax = current.rt.anchorMax; current.homeAnchors = true; }
            yShown = chest;   // Y is the chest's button: with only the bag open it is not shown at all
            float sc = Mathf.Max(0.5f, Plugin.HintScale.Value);
            current.rt.localScale = new Vector3(sc, sc, 1f);
            // lines up with the left edge of the panel's art and may run wider than the panel on the right, so long labels fit
            current.rt.pivot = new Vector2(0f, 1f);
            current.rt.sizeDelta = new Vector2(2f * current.overX + Mathf.Max(0f, Plugin.StripExtraWidth.Value), 34f + 2f * Mathf.Max(0f, Plugin.HintPadding.Value));
            // the gap is measured to the top of the buttons themselves, since there is no panel behind them any more
            float inset = Mathf.Max(0f, Plugin.HintPadding.Value) + 0.65f - Lift;
            current.rt.anchoredPosition = new Vector2(-current.overX, -(current.overY + Plugin.HintOffsetY.Value - inset));
            Core.StripShowing = true;
            // RT and the left stick only have a job on the item grid; everywhere else they show their usual label, dimmed
            Cell(5, false, chest ? "Transfer" : null, null, false, 0f);   // the word follows the screen (a chest is open or not), never the slot
            Cell(6, false, null, null, false, 0f);
            Put(2, yOn, "Fill stacks", yOn ? "Hold \u00b7 Take all" : null, true, yRingNow);
        }

        // Every button always carries a label, so nothing appears and disappears as the focus moves. When a button has
        // nothing to do, its usual label stays in place, dimmed.
        static readonly string[] Defaults = { "Move", "Use", "Fill stacks", "Close", "More", "Drop", "Sort" };
        // The widest each button gets on the item grid, which is the screen the guide is on nearly all the time: its
        // main label and its hold line together. Each button always takes that much room there, so nothing to its
        // right moves as the focus goes from item to item (Move with or without "Hold \u00b7 Split" takes the same room).
        // Words from the rarer screens (the split window, the Favorites window) are not counted, so they cannot
        // stretch a button the rest of the time; if one of them is wider, the button grows only while it shows.
        static readonly string[][] Words =
        {
            new string[] { "Move" },
            new string[] { "Unequip", "Equip", "Use" },
            new string[] { "Fill stacks" },
            new string[0],
            new string[] { "More" },
            new string[] { "Transfer" },   // with a chest open; "Drop" alone is measured apart, for the bag on its own
            new string[] { "Sort" },
        };
        static readonly string[][] SubWords =
        {
            new string[] { "Hold \u00b7 Split" }, new string[] { "Hold \u00b7 Assign" }, new string[] { "Hold \u00b7 Take all" }, new string[0], new string[0], new string[0], new string[] { "Hold" },
        };

        static void Layout(Strip s)
        {
            for (int i = 0; i < 7; i++)
            {
                CellUI c = s.cells[i];
                float w = 0f;
                foreach (string word in Words[i]) w = Mathf.Max(w, c.label.GetPreferredValues(word).x);
                foreach (string word in SubWords[i]) w = Mathf.Max(w, c.sub.GetPreferredValues(word).x);
                s.widest[i] = Mathf.Ceil(w);
                c.box.anchorMin = new Vector2(0f, 0f); c.box.anchorMax = new Vector2(0f, 1f); c.box.pivot = new Vector2(0f, 0.5f);
                c.box.sizeDelta = new Vector2(LabelX + s.widest[i], 0f);
            }
            s.widestDrop = Mathf.Ceil(s.cells[5].label.GetPreferredValues("Drop").x);
            s.cells[3].box.gameObject.SetActive(false);   // B closes, as everywhere, and is not listed
            s.laid = true;
        }
        // on:  the button's tap does something right now.
        // sub: the button's hold, or null. A hold can be live while the tap is not (X on a material: nothing to equip,
        //      but it can still be held to make it a favorite). Then the usual label stays, dimmed, and the hold line is lit.
        // Y is the same on every screen of the inventory, so it is set once per frame here and not by each screen.
        static bool yOn;
        static float yRingNow;
        public static void SetY(bool on, float ring) { yOn = on; yRingNow = ring; }

        // Spacing. Each button takes the room of its widest look on the item grid (main label and hold line), and
        // the same clear gap follows every one of them. So the gaps are even when every button shows its full look,
        // and nothing shifts sideways as the focus moves. Run once a frame, after every label has been set. Y is left
        // out altogether while no chest is open, since it has nothing to do then.
        const float ItemGap = 26f;
        static float lastWidth = 600f;
        static bool yShown;
        static readonly int[] Order = { 4, 0, 1, 2, 5, 6 };   // on screen: LT, A, X, Y, RT, LS
        public static void Arrange()
        {
            if (current == null || current.root == null || !current.root.activeSelf || !current.laid) return;
            float x = 0f;
            foreach (int i in Order)
            {
                CellUI c = current.cells[i];
                bool show = i != 2 || yShown;
                if (c.box.gameObject.activeSelf != show) c.box.gameObject.SetActive(show);
                if (!show) continue;
                // the room this button always takes, or more only if what it shows right now is wider still
                float w = Mathf.Max(string.IsNullOrEmpty(c.label.text) ? 0f : c.label.preferredWidth, string.IsNullOrEmpty(c.sub.text) ? 0f : c.sub.preferredWidth);
                w = Mathf.Max(w, i == 5 && !yShown ? current.widestDrop : current.widest[i]);
                c.box.anchoredPosition = new Vector2(x, 0f);
                x += LabelX + Mathf.Ceil(w) + ItemGap;
            }
            lastWidth = Mathf.Max(0f, x - ItemGap);   // its width, for centring it in the Controller Hotbar window
        }

        public static void Cell(int i, bool on, string label, string sub, bool hasHold, float ring)
        {
            if (i == 3 || i == 2) return;   // B is not listed any more; Y is set by SetY
            Put(i, on, label, sub, hasHold, ring);
        }

        static void Put(int i, bool on, string label, string sub, bool hasHold, float ring)
        {
            if (current == null) return;
            CellUI c = current.cells[i];
            bool hold = sub != null;
            SetKey(c, Glyph(c.keyName));
            c.glyph.alpha = on || hold ? 1f : 0.22f;
            string text = string.IsNullOrEmpty(label) ? Defaults[i] : label;
            if (c.label.text != text) c.label.text = text;
            c.label.alpha = on ? 1f : 0.3f;
            string under = hold ? sub : "";
            if (c.sub.text != under) c.sub.text = under;
            c.label.rectTransform.anchoredPosition = new Vector2(LabelX, Lift + (hold ? 8f : 0f));
            c.sub.rectTransform.anchoredPosition = new Vector2(LabelX, Lift - 10f);
            c.track.enabled = hold && hasHold;
            c.fill.fillAmount = hold ? ring : 0f;
        }

        public static void Extra(string tag, bool on, string label)
        {
            if (current == null) return;
            CellUI c = current.cells[4];
            // The whole D-pad is not a bindable button, so its vanilla glyph is reached by swapping the name inside a direction's glyph tag.
            bool pad = tag == "DPAD";
            string g = pad ? DpadGlyph() : Glyph(tag == "LS" ? "JoyLStick" : "JoyLTrigger");
            c.dpadIcon.enabled = pad && g == "";   // the mod's own white icon is only a fallback
            SetKey(c, g);
            c.glyph.alpha = on ? 1f : 0.22f;
            string text = string.IsNullOrEmpty(label) ? "More" : label;
            if (c.label.text != text) c.label.text = text;
            c.label.alpha = on ? 1f : 0.3f;
            c.sub.text = "";
            c.label.rectTransform.anchoredPosition = new Vector2(LabelX, Lift);
        }
        public static Sprite Box() { if (box == null) MakeSprites(); return box; }
        static Sprite plate;
        // A filled square with the same rounded corners as Box, for anything that fills a favorite's square. A plain
        // square fill showed its sharp corners outside the rounded frame.
        // the same rounded plate drawn at another size, so its corners keep the plate's radius (the 54 plate shown smaller
        // shrinks its corners with it)
        static readonly Dictionary<int, Sprite> platesBySize = new Dictionary<int, Sprite>();
        // a soft round glow: full in the middle, fading to nothing at the edge
        static Sprite glow;
        public static Sprite Glow()
        {
            if (glow != null) return glow;
            const int p = 64;
            Color32[] px = new Color32[p * p];
            float c = (p - 1) / 2f;
            for (int y = 0; y < p; y++)
                for (int x = 0; x < p; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / (p / 2f);
                    float a = Mathf.Clamp01(1f - d); a = a * a;
                    px[y * p + x] = new Color(1f, 1f, 1f, a);
                }
            glow = ToSprite(px, p, Vector4.zero);
            return glow;
        }
        public static Sprite Plate(int p) { return Plate(p, PlateRadius); }
        public static Sprite Plate(int p, float radius)
        {
            Sprite s;
            int key = p * 1000 + Mathf.RoundToInt(radius * 10f);
            if (platesBySize.TryGetValue(key, out s) && s != null) return s;
            Color32[] px = new Color32[p * p];
            float c = (p - 1) / 2f;
            for (int y = 0; y < p; y++)
                for (int x = 0; x < p; x++)
                    px[y * p + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - RoundBox(Mathf.Abs(x - c), Mathf.Abs(y - c), p / 2f, p / 2f, radius)));
            s = ToSprite(px, p, Vector4.zero);
            platesBySize[key] = s;
            return s;
        }
        public static Sprite Plate()
        {
            if (plate != null) return plate;
            const int p = 54;
            Color32[] px = new Color32[p * p];
            float c = (p - 1) / 2f;
            for (int y = 0; y < p; y++)
                for (int x = 0; x < p; x++)
                    px[y * p + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - RoundBox(Mathf.Abs(x - c), Mathf.Abs(y - c), p / 2f, p / 2f, PlateRadius)));
            plate = ToSprite(px, p, Vector4.zero);
            return plate;
        }
        // A frame to stand around a square plate (6 corner radius) with a gap: its corners are the plate's radius plus the
        // gap on the inside and plus the line on the outside, so the two read as concentric. Drawn at exactly its final
        // size and shown unstretched, as the plate is: a stretchable (sliced) frame had its corners shrunk by the game's
        // canvas and looked pointy beside the plate.
        static readonly Dictionary<int, Sprite> frames = new Dictionary<int, Sprite>();
        // Drawn at four times the detail, to be shown at exactly plate + 2 * (gap + line) across (FrameSize), so a line
        // thinner than one unit still comes out clean.
        public static float FrameSize(float plate, float gap, float line) { return plate + 2f * (gap + line); }
        public static Sprite Frame(float plate, float gap, float line)
        {
            const float k = 4f;
            float size = FrameSize(plate, gap, line);
            int m = Mathf.CeilToInt(size * k);
            int key = Mathf.RoundToInt(size * 100f) * 100000 + Mathf.RoundToInt(gap * 100f) * 1000 + Mathf.RoundToInt(line * 100f);
            Sprite sp;
            if (frames.TryGetValue(key, out sp) && sp != null) return sp;
            float inner = (PlateRadius + gap - FrameRoundLess) * k, outer = inner + line * k, c = (m - 1) / 2f, h = size * k / 2f, w = line * k;
            Color32[] px = new Color32[m * m];
            for (int y = 0; y < m; y++)
                for (int x = 0; x < m; x++)
                {
                    float ax = Mathf.Abs(x - c), ay = Mathf.Abs(y - c);
                    float a = Mathf.Clamp01(0.5f - RoundBox(ax, ay, h, h, outer)) * Mathf.Clamp01(0.5f + RoundBox(ax, ay, h - w, h - w, inner));
                    px[y * m + x] = new Color(1f, 1f, 1f, a);
                }
            sp = ToSprite(px, m, Vector4.zero);
            frames[key] = sp;
            return sp;
        }
        // A line just inside a slot's own edge, following its corners (LT held, 0.89.3). Drawn at four times the detail.
        public static Sprite InnerLine(float plate, float line)
        {
            const float k = 4f;
            int m = Mathf.CeilToInt(plate * k);
            int key = -(Mathf.RoundToInt(plate * 100f) * 1000 + Mathf.RoundToInt(line * 100f));
            Sprite sp;
            if (frames.TryGetValue(key, out sp) && sp != null) return sp;
            float outer = PlateRadius * k, inner = Mathf.Max(0f, PlateRadius - line) * k, c = (m - 1) / 2f, h = plate * k / 2f, w = line * k;
            Color32[] px = new Color32[m * m];
            for (int y = 0; y < m; y++)
                for (int x = 0; x < m; x++)
                {
                    float ax = Mathf.Abs(x - c), ay = Mathf.Abs(y - c);
                    float a = Mathf.Clamp01(0.5f - RoundBox(ax, ay, h, h, outer)) * Mathf.Clamp01(0.5f + RoundBox(ax, ay, h - w, h - w, inner));
                    px[y * m + x] = new Color(1f, 1f, 1f, a);
                }
            sp = ToSpriteSmooth(px, m, Vector4.zero);
            frames[key] = sp;
            return sp;
        }
        // A soft glow coming in from a slot's edge, as a design tool's inner shadow of that blur (LT held, 0.89.3): half
        // strength right at the edge, fading inward; nothing outside the slot.
        public static Sprite InnerGlow(float plate, float blur)
        {
            const float k = 2f;
            int m = Mathf.CeilToInt(plate * k);
            int key = -(Mathf.RoundToInt(plate * 100f) * 1000 + Mathf.RoundToInt(blur * 100f)) - 500000000;
            Sprite sp;
            if (frames.TryGetValue(key, out sp) && sp != null) return sp;
            float r = PlateRadius * k, c = (m - 1) / 2f, h = plate * k / 2f, sigma = Mathf.Max(0.1f, blur / 2f) * k;
            Color32[] px = new Color32[m * m];
            for (int y = 0; y < m; y++)
                for (int x = 0; x < m; x++)
                {
                    float d = RoundBox(Mathf.Abs(x - c), Mathf.Abs(y - c), h, h, r);   // negative inside
                    float inside = Mathf.Clamp01(0.5f - d);
                    /* a true blur of the slot's shape (0.89.5): what is left of the slot after blurring, so the glow rounds off
                       in the corners. Measured by distance from the edge (0.89.3-0.89.4), the corners showed hard diagonal seams. */
                    float s2 = sigma * 1.41421356f, px0 = x - c, py0 = y - c;
                    float fx = 0.5f * ((1f - Erfc((h - px0) / s2)) + (1f - Erfc((h + px0) / s2)));
                    float fy = 0.5f * ((1f - Erfc((h - py0) / s2)) + (1f - Erfc((h + py0) / s2)));
                    float a = inside * Mathf.Clamp01(1f - fx * fy);
                    px[y * m + x] = new Color(1f, 1f, 1f, a);
                }
            sp = ToSpriteSmooth(px, m, Vector4.zero);
            frames[key] = sp;
            return sp;
        }
        static float Erfc(float x)
        {
            float z = Mathf.Abs(x), t = 1f / (1f + 0.5f * z);
            float r = t * Mathf.Exp(-z * z - 1.26551223f + t * (1.00002368f + t * (0.37409196f + t * (0.09678418f + t * (-0.18628806f + t * (0.27886807f + t * (-1.13520398f + t * (1.48851587f + t * (-0.82215223f + t * 0.17087277f)))))))));
            return x >= 0f ? r : 2f - r;
        }
        public const float PlateRadius = 4f;   // a slot's corner radius (Rob's pen.dev frame, 0.82.1; 6 before)
        const float FrameRoundLess = 0f;   // Rob's numbers: the square 6, the frame's inside edge 8 (6 plus the 2 gap), so none taken off
        public static Sprite Ring() { if (ringSprite == null) MakeSprites(); return ringSprite; }
        public static Sprite Disc() { if (circle == null) MakeSprites(); return circle; }
        static Sprite solid;
        public static Sprite Solid()
        {
            if (solid == null) solid = ToSprite(new Color32[] { Color.white, Color.white, Color.white, Color.white }, 2, Vector4.zero);
            return solid;
        }

        public static GameObject New(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            return go;
        }

        public static RectTransform Place(GameObject go, Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = pivot; rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        public static TMP_Text Text(GameObject go, TMP_Text like, float size, Color color, TextAlignmentOptions align)
        {
            // added while switched off, so the text gets the game's font before it first wakes up: otherwise it looks for
            // a default font the game does not ship, and the log fills with "LiberationSans SDF ... not found" notices
            bool wasOn = go.activeSelf;
            if (wasOn) go.SetActive(false);
            TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
            if (like != null) t.font = like.font;
            if (wasOn) go.SetActive(true);
            t.fontSize = size; t.color = color; t.alignment = align;
            t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Overflow; t.raycastTarget = false;
            t.text = "";
            return t;
        }

        public static Image Img(GameObject go, Sprite sprite, Color color)
        {
            Image im = go.AddComponent<Image>();
            im.sprite = sprite; im.color = color; im.raycastTarget = false;
            return im;
        }

        // If building the guide ever fails, the half-made one is removed and it is not retried every frame.
        static bool buildFailed;
        static Strip SafeBuild(InventoryGui gui, RectTransform panel)
        {
            if (buildFailed || panel == null) return null;
            try { return Build(gui, panel); }
            catch (Exception e)
            {
                buildFailed = true;
                Plugin.Log.LogError("Button guide could not be built: " + e);
                Transform half = panel.Find("ControllerPro_Hints");
                if (half != null) UnityEngine.Object.Destroy(half.gameObject);
                return null;
            }
        }

        static Strip Build(InventoryGui gui, RectTransform panel)
        {
            if (panel == null) return null;
            if (circle == null) MakeSprites();
            TMP_Text like = gui.m_weight != null ? gui.m_weight : (gui.m_armor != null ? gui.m_armor : gui.m_containerName);
            Vector2 mid = new Vector2(0.5f, 0.5f), leftMid = new Vector2(0f, 0.5f);

            TMP_Text floating = null;
            try
            {
                if (KeyHints.instance != null)
                    foreach (TMP_Text t in KeyHints.instance.GetComponentsInChildren<TMP_Text>(true))
                        if (t.font != null && t.font.name.Contains("AveriaSans")) { floating = t; break; }
            }
            catch (Exception) { floating = null; }

            Strip s = new Strip();
            s.root = New("ControllerPro_Hints", panel);
            s.rt = Place(s.root, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 46f));
            Image bg = Img(s.root, null, new Color(0f, 0f, 0f, 0.72f));
            Image art = Plugin.GamePanelStyle.Value ? Style(bg, panel) : null;   // still read, so the strip lines up with the panel's art
            bg.enabled = Plugin.StripBackground.Value;                           // by default the guide floats with no panel behind it
            if (art != null)
            {
                s.overX = Mathf.Max(0f, (art.rectTransform.rect.width - panel.rect.width) * 0.5f);
                s.overY = Mathf.Max(0f, (art.rectTransform.rect.height - panel.rect.height) * 0.5f);
            }

            for (int i = 0; i < 7; i++)
            {
                CellUI c = new CellUI();
                GameObject cell = New("cell" + i, s.root.transform);
                // on screen, left to right: LT (the modifier), A, X, Y, then B (close) at the far right
                int slot = i == 4 ? 0 : Mathf.Min(i + 1, 4);
                Place(cell, new Vector2(slot / 5f, 0f), new Vector2((slot + 1) / 5f, 1f), mid, Vector2.zero, Vector2.zero);   // re-laid by Layout once the labels can be measured
                c.box = (RectTransform)cell.transform;

                GameObject glyph = New("glyph", cell.transform);
                Place(glyph, leftMid, leftMid, mid, new Vector2(24f, Lift), new Vector2(34f, 34f));
                c.glyph = glyph.AddComponent<CanvasGroup>();
                c.glyph.blocksRaycasts = false;

                if (i != 4)
                {
                    // 32.7 across: one screen pixel trimmed off the outside all round, which also thins the ring by a pixel
                    c.track = Img(New("track", glyph.transform), ringSprite, new Color(1f, 1f, 1f, 0.15f));   // the resting ring: 15% (was 20%)
                    Place(c.track.gameObject, mid, mid, mid, Vector2.zero, new Vector2(32.7f, 32.7f));
                    c.fill = Img(New("fill", glyph.transform), ringSprite, Color.white);
                    Place(c.fill.gameObject, mid, mid, mid, Vector2.zero, new Vector2(32.7f, 32.7f));
                    c.fill.type = Image.Type.Filled; c.fill.fillMethod = Image.FillMethod.Radial360;
                    c.fill.fillOrigin = (int)Image.Origin360.Top; c.fill.fillClockwise = true; c.fill.fillAmount = 0f;
                    c.keyName = KeyName[i];
                }
                else
                {
                    c.track = Img(New("track", glyph.transform), null, Color.clear); c.track.enabled = false;
                    c.fill = c.track;
                    c.keyName = "JoyLTrigger";
                    c.dpadIcon = Img(New("dpad", glyph.transform), Dpad(), Color.white);
                    Place(c.dpadIcon.gameObject, mid, mid, mid, Vector2.zero, new Vector2(24f, 24f));
                    c.dpadIcon.enabled = false;
                }
                c.key = Text(New("key", glyph.transform), like, Plugin.GlyphSize.Value, Color.white, TextAlignmentOptions.Center);
                // measured in screenshots: left alone, the game's glyph sits about one screen pixel low of the ring's centre
                Place(c.key.gameObject, mid, mid, mid, new Vector2(-0.1f, 1.05f), new Vector2(34f, 34f));

                // label sizes follow the game's own inventory text (16 bold, 12 for the second line)
                c.label = Text(New("label", cell.transform), like, TextStyle.HintSize, Color.white, TextAlignmentOptions.Left);
                c.label.fontStyle = FontStyles.Bold;
                Place(c.label.gameObject, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), leftMid, Vector2.zero, new Vector2(-LabelX, 20f));
                c.label.rectTransform.offsetMin = new Vector2(LabelX, -10f); c.label.rectTransform.offsetMax = new Vector2(0f, 10f);
                c.sub = Text(New("sub", cell.transform), like, TextStyle.HintSmallSize, new Color(1f, 1f, 1f, TextStyle.HintSmallAlpha), TextAlignmentOptions.Left);
                Place(c.sub.gameObject, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), leftMid, Vector2.zero, new Vector2(-LabelX, 14f));
                c.sub.rectTransform.offsetMin = new Vector2(LabelX, -7f); c.sub.rectTransform.offsetMax = new Vector2(0f, 7f);
                // With no panel behind the strip the text has to read over the world. The game's own floating hints
                // (Attack, Block, Dodge) already do, so the labels borrow that text style.
                if (floating != null)
                {
                    c.label.font = floating.font; c.label.fontSharedMaterial = floating.fontSharedMaterial;
                    c.sub.font = floating.font; c.sub.fontSharedMaterial = floating.fontSharedMaterial;
                }
                s.cells[i] = c;
            }
            s.root.SetActive(false);
            return s;
        }

        // Gives the strip the same background art as the panel it hangs from, so it reads as part of the game's UI.
        // The panel's layout is also written to the log once, to help pick the right piece if this guess is off.
        public static Image Style(Image bg, RectTransform panel)
        {
            try
            {
                Image src = null;
                Transform named = panel.Find("Bkg");
                if (named == null) named = panel.Find("bkg");
                if (named != null) src = named.GetComponent<Image>();
                float panelArea = Mathf.Abs(panel.rect.width * panel.rect.height), best = 0f;
                System.Text.StringBuilder sb = new System.Text.StringBuilder("Panel layout for '" + panel.name + "' (" + panel.rect.width + " x " + panel.rect.height + "):");
                foreach (Image im in panel.GetComponentsInChildren<Image>(true))
                {
                    if (im == bg || im.transform.IsChildOf(bg.transform)) continue;
                    Rect r = im.rectTransform.rect;
                    int depth = 0;
                    for (Transform t = im.transform; t != panel && t != null; t = t.parent) depth++;
                    if (depth <= 2) sb.Append("\n  depth " + depth + "  " + im.name + "  sprite=" + (im.sprite != null ? im.sprite.name : "none") + "  type=" + im.type + "  size=" + r.width + " x " + r.height + "  color=" + im.color);
                    // otherwise guess: the largest image near the top of the panel that has art and covers most of it
                    float area = Mathf.Abs(r.width * r.height);
                    if (named == null && depth <= 1 && im.sprite != null && area > best && area >= panelArea * 0.6f) { best = area; src = im; }
                }
                Plugin.Log.LogInfo(sb.ToString());
                if (src == null || src.sprite == null) { Plugin.Log.LogInfo("Hint strip: no panel background found, keeping the flat style"); return null; }
                bg.sprite = src.sprite; bg.type = src.type; bg.material = src.material; bg.color = src.color;
                bg.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier;
                Plugin.Log.LogInfo("Hint strip: using panel background '" + src.name + "' (sprite " + src.sprite.name + ")");
                return src;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Hint strip style: " + e.Message); return null; }
        }

        static void MakeSprites()
        {
            const int n = 64;
            Color32[] disc = new Color32[n * n], ring = new Color32[n * n];
            float c = (n - 1) / 2f, r = n / 2f - 1f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float inDisc = Mathf.Clamp01(r - d + 0.5f);
                    float inRing = inDisc * Mathf.Clamp01(d - (r - 5.5f) + 0.5f);
                    disc[y * n + x] = new Color(1f, 1f, 1f, inDisc);
                    ring[y * n + x] = new Color(1f, 1f, 1f, inRing);
                }
            circle = ToSprite(disc, n, Vector4.zero);
            ringSprite = ToSprite(ring, n, Vector4.zero);

            const int m = 32;
            Color32[] frame = new Color32[m * m];
            for (int y = 0; y < m; y++)
                for (int x = 0; x < m; x++)
                {
                    // rounded rectangle outline, 2px line, 6px corner radius
                    float dx = Mathf.Max(0f, Mathf.Abs(x - (m - 1) / 2f) - (m / 2f - 7f));
                    float dy = Mathf.Max(0f, Mathf.Abs(y - (m - 1) / 2f) - (m / 2f - 7f));
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(6f - d + 0.5f) * Mathf.Clamp01(d - 4f + 0.5f);
                    frame[y * m + x] = new Color(1f, 1f, 1f, a);
                }
            box = ToSprite(frame, m, new Vector4(10f, 10f, 10f, 10f));

            // D-pad icon: two rounded bars crossed
            Color32[] cross = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float px = Mathf.Abs(x - c), py = Mathf.Abs(y - c);
                    float d = Mathf.Min(RoundBox(px, py, 30f, 10f, 4f), RoundBox(px, py, 10f, 30f, 4f));
                    cross[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d));
                }
            dpad = ToSprite(cross, n, Vector4.zero);
        }

        // distance to a rounded box centred on the origin (negative inside)
        static float RoundBox(float px, float py, float hx, float hy, float r)
        {
            float qx = px - (hx - r), qy = py - (hy - r);
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        // As ToSprite, with smaller copies kept for drawing smaller than drawn (0.89.4): a fine line drawn at four times the
        // detail and shown shrunk lost pixels and came out thin and broken without them
        static Sprite ToSpriteSmooth(Color32[] px, int size, Vector4 border)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            tex.SetPixels32(px); tex.filterMode = FilterMode.Trilinear; tex.wrapMode = TextureWrapMode.Clamp; tex.Apply(true, true);
            tex.hideFlags = HideFlags.HideAndDontSave;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        }
        static Sprite ToSprite(Color32[] px, int size, Vector4 border)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels32(px); tex.Apply(false, true);
            tex.hideFlags = HideFlags.HideAndDontSave;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        }
    }

    // ---------- favorites ----------
    // Two sets of four. The primary set is on the D-pad. The secondary set is on the face buttons while LT is held.
    // A slot holds one item, or a one-handed weapon plus a shield as a set. A favorite stays until another item is
    // assigned over it; if the item is not in the bag the slot greys out.
    // Using a favorite goes through the same call as the vanilla hotbar, so equip timing is untouched.
    static class Favorites
    {
        // slots 0-3: D-pad up, down, left, right. Slots 4-7: Y, A, X, B, which sit in the same four positions on the pad.
        const int Up = 0, Down = 1, Left = 2, Right = 3, Slots = 8;
        static readonly string[] Pad = { "JoyDPadUp", "JoyDPadDown", "JoyDPadLeft", "JoyDPadRight" };
        static readonly string[] Face = { "JoyButtonY", "JoyButtonA", "JoyButtonX", "JoyButtonB" };
        const string DataKey = "ControllerPro.Favorites";

        static readonly string[] main = new string[Slots], shield = new string[Slots];
        static Player loadedFor;
        static readonly MethodInfo M_TakeInput = AccessTools.Method(typeof(Player), "TakeInput");
        static readonly System.Collections.Generic.Dictionary<string, ItemDrop.ItemData> prefabData = new System.Collections.Generic.Dictionary<string, ItemDrop.ItemData>();

        static float downStart;
        static bool downTracking, downFired;

        // ----- data -----
        static void EnsureLoaded(Player p)
        {
            if (loadedFor == p) return;
            loadedFor = p;
            rowOn = false;   // a new character: its own favorites, nothing put aside
            for (int i = 0; i < Slots; i++) { main[i] = null; shield[i] = null; }
            string raw;
            if (!p.m_customData.TryGetValue(DataKey, out raw) || string.IsNullOrEmpty(raw)) return;
            string[] parts = raw.Split(';');
            for (int i = 0; i < Slots && i < parts.Length; i++)
            {
                string[] pair = parts[i].Split('|');
                main[i] = pair[0].Length > 0 ? pair[0] : null;
                shield[i] = pair.Length > 1 && pair[1].Length > 0 ? pair[1] : null;
            }
        }

        // ----- Top row assignment -----
        // With AssignStyle TopRow the bag's top row is the hotbar: column 1 to 4 are the D-pad's left, up, down, right and
        // column 5 to 8 the face buttons' X, Y, A, B (the order the row hotbar shows them). The favorites are read from that
        // row as it is, every frame; one item per button (no weapon and shield sets). The window's own favorites are put
        // aside untouched while this is on, never saved over, and come back when Window is picked again.
        // Top row is the hotbar's only assignment since 0.80.0 (Rob): the Controller Hotbar window, its "Hold u00b7 Assign" and LT + Start
        // are archived (their code is kept below, unused, as the level diamonds were). The setting AssignStyle is gone.
        public static bool TopRow { get { return true; } }
        static readonly int[] ColumnSlot = { Left, Up, Down, Right, 6, 4, 5, 7 };   // face slots: 4 Y, 5 A, 6 X, 7 B
        static string[] asideMain, asideShield;
        static bool rowOn;
        public static void SyncTopRow(Player p)
        {
            if (p == null) return;
            bool want = TopRow;
            if (want && !rowOn) { asideMain = (string[])main.Clone(); asideShield = (string[])shield.Clone(); rowOn = true; }
            else if (!want && rowOn) { Array.Copy(asideMain, main, Slots); Array.Copy(asideShield, shield, Slots); rowOn = false; return; }
            if (!want) return;
            Inventory inv = p.GetInventory();
            bool two = SecondaryAvailable();
            for (int c = 0; c < ColumnSlot.Length; c++)
            {
                int slot = ColumnSlot[c];
                ItemDrop.ItemData it = inv != null && (slot < 4 || two) ? inv.GetItemAt(c, 0) : null;
                main[slot] = NameOf(it); shield[slot] = null;
            }
        }

        static void Save(Player p)
        {
            if (rowOn) return;   // the top row is not saved as favorites: the window's own stay as they were
            string[] parts = new string[Slots];
            for (int i = 0; i < Slots; i++) parts[i] = (main[i] ?? "") + "|" + (shield[i] ?? "");
            p.m_customData[DataKey] = string.Join(";", parts); // stored with the character, on this PC only
        }

        // ----- the assign overlay: a small inventory of favorites -----
        // A focus moves over the squares of both sets. A picks up what is on a square, or puts down what is in hand.
        // It always opens with the item it was opened for already in hand. Anything already on a favorite can be picked
        // up and moved too, so the whole arrangement can be reordered in one visit.
        //   - A new item put on a taken square replaces what was there (a shield still joins a one-handed weapon as a set).
        //   - Something picked up from one square and put on another taken square trades places with it, as in the bag.
        // The ninth place, between the two sets: a holding slot. A newly favorited item starts over it. Anything can be
        // set down in it, and it can sit there for a while, but it is not a favorite: whatever is in it when the window
        // closes is dropped. It never trades places. Putting a second thing in it throws the first away. That makes
        // it the way to clear a square: lift the item and set it down here.
        const int Home = 8;
        static string holdMain, holdShield;
        static int pickFocus;
        static bool carrying;
        static string carryMain, carryShield, pickMarked;
        static int carryFrom;   // the square the thing in hand came from, or -1 for a new item
        static int navDir;
        static float navNext;
        // where each square sits, for moving the focus: D-pad set on the left, face-button set on the right, Home between and above
        static readonly Vector2[] PickAt = { new Vector2(-2f, 1f), new Vector2(-2f, -1f), new Vector2(-3f, 0f), new Vector2(-1f, 0f),
                                             new Vector2(2f, 1f), new Vector2(2f, -1f), new Vector2(1f, 0f), new Vector2(3f, 0f),
                                             new Vector2(0f, 1.2f) };
        public static bool PickCarrying { get { return carrying; } }
        static bool pickCursor, pickOver;
        static Vector2 pickPos;
        static GameObject pickCursorRoot;
        static float pickHoverK;
        // a square's (or the holding slot's) box on screen
        const int Last = Home;
        static bool Usable(int i, bool two) { return !(i >= 4 && i < Home && !two); }   // the squares and the holding slot
        // where each place sits for moving the focus; in the row layout the two sets are rows side by side
        static Vector2 NavAt(int i)
        {
            if (!Plugin.RowHotbar || i >= Home) return PickAt[i];
            float x = RowX[i % 4];
            return new Vector2(i < 4 ? x - 2.6f : x + 2.6f, 0f);
        }

        // The window takes the hotbar's own style: the cross (two D-pad shaped sets) or the row (each set a row of four,
        // each square's button under it, as on the row hotbar). Laid out again whenever the style changes.
        static void PickLayout(bool row, bool two)
        {
            bool compact = !row && Plugin.CompactHotbar;
            int key = (row ? 1 : 0) + (two ? 2 : 0) + (compact ? 4 : 0);
            if (pickLaid != key && pickCross1 != null)
            {
                pickLaid = key;
                float below = -(SquareSize * 0.5f + RowGlyphGap);
                for (int i = 0; i < 4; i++)
                {
                    Vector2 at = row ? new Vector2(RowX[i] * RowPitch, 0f) : compact ? CompactAt(i) : Offset[i];
                    if (pickSquares[i] != null) ((RectTransform)pickSquares[i].border.transform.parent).anchoredPosition = at;
                    if (pickSquares2[i] != null) ((RectTransform)pickSquares2[i].border.transform.parent).anchoredPosition = at;
                    if (pickPadGlyphs != null) { pickPadGlyphs[i].rectTransform.anchoredPosition = compact ? CompactEdge(i) : at + new Vector2(0f, below); pickPadGlyphs[i].gameObject.SetActive(row || compact); pickPadGlyphs[i].fontSize = compact ? 18f : 15f; }
                    Vector2 faceAt = row ? at + new Vector2(0f, below) : compact ? CompactCorner(i) : i == Up ? new Vector2(0f, DiamondAt) : i == Down ? new Vector2(0f, -DiamondAt) : i == Left ? new Vector2(-DiamondAt, 0f) : new Vector2(DiamondAt, 0f);
                    if (pickFace != null) { pickFace[i].rectTransform.anchoredPosition = faceAt; pickFace[i].fontSize = row ? 15f : compact ? 18f : DiamondSize; }
                }
                // the sets: crosses 150 out from the middle; rows far enough apart for four squares each (sets drawn at 1.3)
                float side = two ? (row ? (1.5f * RowPitch + SquareSize * 0.5f) * 1.3f + 36f : compact ? 180f : 150f) : 0f;   // the compact cross a little further apart, for its side glyphs
                float setY = row ? 0f : -15f, labelY = row ? -100f : -164f;
                ((RectTransform)pickCross1.transform).anchoredPosition = new Vector2(-side, setY);
                ((RectTransform)pickCross2.transform).anchoredPosition = new Vector2(side, setY);
                pickLabel1.rectTransform.anchoredPosition = new Vector2(-side, labelY);
                pickLabel2.rectTransform.anchoredPosition = new Vector2(side, labelY);
                if (pickHome != null) ((RectTransform)pickHome.transform).anchoredPosition = new Vector2(0f, row ? 96f : 78f);
                float w = row ? (two ? 2f * side + 2f * (1.5f * RowPitch + SquareSize * 0.5f) * 1.3f + 60f : 460f) : (two ? (compact ? 700f : 620f) : 400f);
                pickPanel.sizeDelta = new Vector2(w, row ? 330f : 390f);
            }
            // the cross's middle glyph has no place in a row or the compact cross; each square shows its own glyph instead
            if (row || compact)
            {
                if (pickDpadGlyph != null && pickDpadGlyph.text != "") pickDpadGlyph.text = "";
                if (pickDpadIcon != null && pickDpadIcon.enabled) pickDpadIcon.enabled = false;
                if (pickPadGlyphs != null) for (int i = 0; i < 4; i++) { string g = Hints.Glyph(Pad[i]); if (pickPadGlyphs[i].text != g) pickPadGlyphs[i].text = g; }
            }
        }
        static RectTransform PickRect(int i)
        {
            if (i >= Home) return pickHome != null ? pickHome.transform as RectTransform : null;
            Square q = i < 4 ? pickSquares[i] : pickSquares2[i - 4];
            return q != null && q.border != null ? q.border.rectTransform : null;
        }
        static Vector2 PickWorld(int i)
        {
            RectTransform rt = PickRect(i);
            return rt != null ? (Vector2)rt.position : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }

        static int SlotOf(string name)
        {
            if (name == null) return -1;
            for (int i = 0; i < Slots; i++) if (main[i] == name || shield[i] == name) return i;
            return -1;
        }
        // Can A do anything right now? Putting down needs a square under the focus; picking up needs something on it.
        public static bool PickCanA()
        {
            if (pickCursor && !pickOver) return false;   // the cursor is over nothing
            if (carrying) return true;
            if (pickFocus >= Home) return holdMain != null || holdShield != null;
            return main[pickFocus] != null || shield[pickFocus] != null;
        }

        // A new item going onto a square. A shield joins a one-handed weapon already there (and the other way round) as
        // a set; anything else takes the whole square, and what was there is no longer a favorite.
        static void Put(int slot, string name)
        {
            ItemDrop.ItemData d = Prefab(name);
            ItemDrop.ItemData cur = main[slot] != null ? Prefab(main[slot]) : null;
            if (d != null && IsShield(d) && TakesShield(cur)) shield[slot] = name;
            else if (d != null && TakesShield(d) && IsShield(cur) && shield[slot] == null) { shield[slot] = main[slot]; main[slot] = name; }
            else if (d != null && TakesShield(d) && shield[slot] != null) main[slot] = name;
            else { main[slot] = name; shield[slot] = null; }
        }

        public static void BeginEdit(ItemDrop.ItemData item)
        {
            BeginEdit();
            bool two = SecondaryAvailable();
            string name = NameOf(item);
            pickMarked = name;
            int at = SlotOf(name);
            if (at >= 4 && !two) { if (shield[at] == name) shield[at] = null; else { main[at] = shield[at]; shield[at] = null; } at = -1; }
            carrying = true; navDir = 0; holdMain = holdShield = null; pickCursor = false; pickOver = false;
            if (at >= 0)
            {
                // already a favorite: it comes off its square (with its set partner) and into the hand
                carryMain = main[at]; carryShield = shield[at]; main[at] = null; shield[at] = null;
                carryFrom = at; pickFocus = at;
            }
            else
            {
                // new: it starts in hand over the holding slot in the middle, and is carried onto a square from there
                carryMain = name; carryShield = null; carryFrom = -1;
                pickFocus = Home;
            }
        }

        public static void PickInput(bool aPressed)
        {
            bool two = SecondaryAvailable();
            Vector2 st = ZInput.GetJoyLeftStick();
            int dx = Core.RawHeld("JoyDPadLeft") ? -1 : Core.RawHeld("JoyDPadRight") ? 1 : 0;
            int dy = Core.RawHeld("JoyDPadUp") ? 1 : Core.RawHeld("JoyDPadDown") ? -1 : 0;
            // The controller cursor works here as it does in the bag: the left stick brings it up, the D-pad puts it
            // away and goes back to stepping from square to square. Whatever square (or the holding slot) the cursor
            // is over is the focus; over nothing, there is no focus and A does nothing.
            bool dpad = dx != 0 || dy != 0;
            if (!Plugin.CursorEnabled.Value || pickHome == null) pickCursor = false;
            else if (!pickCursor) { if (!dpad && st.magnitude > 0.35f) { pickCursor = true; pickOver = true; pickPos = PickWorld(pickFocus); } }
            else if (dpad) pickCursor = false;
            if (pickCursor)
            {
                float mag = st.magnitude;
                if (mag > 0.15f)
                {
                    float speed = (pickOver ? Plugin.CursorSlotSpeed.Value : Plugin.CursorPanelSpeed.Value) * (Screen.height / 1080f) * Mathf.Pow(Mathf.Min(1f, mag), 1.6f);
                    pickPos += st / mag * speed * Time.unscaledDeltaTime;
                    pickPos.x = Mathf.Clamp(pickPos.x, 4f, Screen.width - 4f); pickPos.y = Mathf.Clamp(pickPos.y, 4f, Screen.height - 4f);
                }
                int hit = -1;
                for (int i = 0; i <= Last && hit < 0; i++)
                {
                    if (!Usable(i, two)) continue;
                    RectTransform rt = PickRect(i);
                    if (rt != null && RectTransformUtility.RectangleContainsScreenPoint(rt, pickPos, null)) hit = i;
                }
                pickOver = hit >= 0;
                if (hit >= 0) pickFocus = hit;
                dx = dy = 0;   // the stick steers the cursor, it does not step
            }
            else if (!Plugin.CursorEnabled.Value && dx == 0 && dy == 0 && st.magnitude > 0.5f)
            {
                // with the cursor switched off in the settings, the stick steps like the D-pad
                if (Mathf.Abs(st.x) >= Mathf.Abs(st.y)) dx = st.x > 0f ? 1 : -1; else dy = st.y > 0f ? 1 : -1;
            }
            if (dx != 0) dy = 0;
            int dir = dx > 0 ? 1 : dx < 0 ? 2 : dy > 0 ? 3 : dy < 0 ? 4 : 0;
            float now = Time.unscaledTime;
            if (dir == 0) navDir = 0;
            else if (dir != navDir || now >= navNext)
            {
                // one flick moves one square, then repeats while held
                navNext = now + (dir != navDir ? 0.28f : 0.14f);
                navDir = dir;
                Vector2 from = NavAt(pickFocus), d = new Vector2(dx, dy);
                int best = -1; float bestScore = float.MaxValue;
                for (int i = 0; i <= Last; i++)
                {
                    if (i == pickFocus || !Usable(i, two)) continue;
                    Vector2 v = NavAt(i) - from;
                    float along = Vector2.Dot(v, d);
                    if (along <= 0.01f) continue;
                    float score = along + 2f * Mathf.Abs(v.x * d.y - v.y * d.x);   // straight ahead beats off to the side
                    if (score < bestScore) { bestScore = score; best = i; }
                }
                if (best >= 0) pickFocus = best;
            }

            if (!aPressed || (pickCursor && !pickOver)) return;
            int t = pickFocus;
            if (t >= Home)
            {
                // the holding slot: setting something down throws away what was already in it; nothing trades places here
                if (carrying) { holdMain = carryMain; holdShield = carryShield; carrying = false; carryMain = carryShield = null; }
                else if (holdMain != null || holdShield != null) { carryMain = holdMain; carryShield = holdShield; holdMain = holdShield = null; carryFrom = Home; carrying = true; pickMarked = carryMain ?? carryShield; }
                return;
            }
            if (carrying)
            {
                if (carryFrom >= 0 && carryFrom < Home)
                {
                    // lifted from another square: the two trade places, as items do in the bag
                    string wasMain = main[t], wasShield = shield[t];
                    main[t] = carryMain; shield[t] = carryShield;
                    if (carryFrom != t) { main[carryFrom] = wasMain; shield[carryFrom] = wasShield; }
                }
                else if (carryShield == null && carryMain != null) Put(t, carryMain);   // a single item, new or from the holding slot
                else { main[t] = carryMain; shield[t] = carryShield; }                   // a set from the holding slot takes the square
                pickMarked = carryMain ?? carryShield;   // blue marks the last thing handled
                carrying = false; carryMain = carryShield = null;
                AssignSound();
            }
            else if (main[t] != null || shield[t] != null)
            {
                carryMain = main[t]; carryShield = shield[t]; main[t] = null; shield[t] = null;
                carryFrom = t; carrying = true;
                pickMarked = carryMain ?? carryShield;
            }
        }
        public static void BeginEdit()
        {
            if (Player.m_localPlayer != null) EnsureLoaded(Player.m_localPlayer);
        }
        // Opened with LT + Start: nothing in hand, the focus on the D-pad set's top square, ready to rearrange.
        public static void BeginEditEmpty()
        {
            BeginEdit();
            pickMarked = null; carrying = false; carryMain = carryShield = null; carryFrom = -1;
            holdMain = holdShield = null; navDir = 0; pickCursor = false; pickOver = false; pickFocus = Up;
        }
        // Opened with LT + Start the window stands on its own: the bag it is drawn over is hidden while it is up (when the
        // window does not sit inside the bag's own objects), and comes back when it closes.
        static CanvasGroup quickCovered; static bool quickCovering;
        public static void QuickCover(bool on)
        {
            InventoryGui gui = InventoryGui.instance;
            Transform inv = gui != null ? gui.m_inventoryRoot : null;
            if (!on || inv == null || pickRoot == null || pickRoot.transform.IsChildOf(inv))
            {
                if (quickCovering && quickCovered != null) { quickCovered.alpha = 1f; quickCovered.blocksRaycasts = true; }
                quickCovering = false;
                return;
            }
            if (quickCovered == null || quickCovered.transform != inv) { quickCovered = inv.GetComponent<CanvasGroup>(); if (quickCovered == null) quickCovered = inv.gameObject.AddComponent<CanvasGroup>(); }
            quickCovering = true;
            quickCovered.alpha = 0f; quickCovered.blocksRaycasts = false;
        }
        // LT held in play: Start opens the Controller Hotbar window straight away (and the game's menu does not open)
        public static bool QuickChord { get { return !TopRow && Plugin.Enabled.Value && Plugin.FavEnabled.Value && ZInput.IsGamepadActive() && Player.m_localPlayer != null && !InventoryGui.IsVisible() && !Menu.IsVisible() && Core.RawHeld("JoyLTrigger"); } }
        // Leaving the Favorites window. Everything done in it is kept. What was still in hand goes back to the square
        // it was lifted from (a new item that was never put down is simply not a favorite), and whatever was left in
        // the holding slot is dropped.
        public static void FinishEdit()
        {
            if (carrying && carryFrom >= 0 && carryFrom < Home) { main[carryFrom] = carryMain; shield[carryFrom] = carryShield; }
            carrying = false; carryMain = carryShield = null; holdMain = holdShield = null;
            Commit();
        }
        public static void Commit() { if (Player.m_localPlayer != null) Save(Player.m_localPlayer); }

        static ItemDrop.ItemData Prefab(string name)
        {
            ItemDrop.ItemData data;
            if (prefabData.TryGetValue(name, out data)) return data;
            data = null;
            if (ObjectDB.instance != null)
            {
                GameObject go = ObjectDB.instance.GetItemPrefab(name);
                ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
                if (drop != null) data = drop.m_itemData;
            }
            prefabData[name] = data;
            return data;
        }

        static bool IsShield(ItemDrop.ItemData d) { return d != null && d.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield; }
        static bool TakesShield(ItemDrop.ItemData d)
        {
            if (d == null) return false;
            ItemDrop.ItemData.ItemType t = d.m_shared.m_itemType;
            return t == ItemDrop.ItemData.ItemType.OneHandedWeapon || t == ItemDrop.ItemData.ItemType.Torch;
        }

        static string NameOf(ItemDrop.ItemData it) { return it != null && it.m_dropPrefab != null ? it.m_dropPrefab.name : null; }

        static ItemDrop.ItemData Find(Player p, string name)
        {
            if (name == null) return null;
            ItemDrop.ItemData found = null;
            foreach (ItemDrop.ItemData it in p.GetInventory().GetAllItems())
            {
                if (NameOf(it) != name) continue;
                if (p.IsItemEquiped(it)) return it;
                if (found == null) found = it;
            }
            return found;
        }

        // The glyph of the button an item is on: its D-pad direction, or for the LT set its face button. Null if none.
        // the button a top-row column is on: columns 1-4 the D-pad, 5-8 the face buttons (those only where LT is free)
        public static string ColumnGlyph(int col)
        {
            if (col < 0 || col >= ColumnSlot.Length) return null;
            int slot = ColumnSlot[col];
            if (slot >= 4 && !SecondaryAvailable()) return null;
            return slot < 4 ? Hints.Glyph(Pad[slot]) : Hints.Glyph(Face[slot - 4]);
        }

        // One press, one activation. The game's own "button down" repeats while a D-pad direction is held (it is built
        // for scrolling menus), which made a held direction fire a favorite over and over. These are true once per press.
        static readonly bool[] padHeld = new bool[4], faceHeld = new bool[4], padDown = new bool[4], faceDown = new bool[4];
        static void Poll()
        {
            for (int i = 0; i < 4; i++)
            {
                bool p = ZInput.GetButton(Pad[i]), f = ZInput.GetButton(Face[i]);
                padDown[i] = p && !padHeld[i]; padHeld[i] = p;
                faceDown[i] = f && !faceHeld[i]; faceHeld[i] = f;
            }
        }

        // The game's own equip sound, played when an item is put on a favorite. It is played on this PC only: the
        // sound is made as a plain local object, not a networked one, so nobody nearby hears an equip that did not happen.
        public static void AssignSound() { if (Plugin.FavAssignSound.Value) EquipSound(); }
        public static void EquipSound()
        {
            Player p = Player.m_localPlayer;
            if (p == null) return;
            try
            {
                EffectList list = p.m_equipEffects;
                if (list == null || list.m_effectPrefabs == null) return;
                foreach (EffectList.EffectData d in list.m_effectPrefabs)
                {
                    if (d == null || !d.m_enabled || d.m_prefab == null) continue;
                    bool was = ZNetView.m_forceDisableInit;
                    ZNetView.m_forceDisableInit = true;
                    try { UnityEngine.Object.Instantiate(d.m_prefab, p.transform.position, Quaternion.identity); }
                    finally { ZNetView.m_forceDisableInit = was; }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Assign sound: " + e.Message); }
        }

        public static void Assign(int slot, ItemDrop.ItemData item)
        {
            Player p = Player.m_localPlayer;
            string name = NameOf(item);
            if (p == null || name == null) return;
            EnsureLoaded(p);
            for (int i = 0; i < Slots; i++)   // an item lives in one slot only
            {
                if (shield[i] == name) shield[i] = null;
                if (main[i] == name) { main[i] = shield[i]; shield[i] = null; }
            }
            ItemDrop.ItemData cur = main[slot] != null ? Prefab(main[slot]) : null;
            if (IsShield(item) && TakesShield(cur)) shield[slot] = name;                           // weapon already there: join it as a set
            else if (TakesShield(item) && IsShield(cur) && shield[slot] == null) { shield[slot] = main[slot]; main[slot] = name; } // shield already there: weapon joins it
            else if (TakesShield(item)) main[slot] = name;                                          // swap the weapon, keep the shield
            else { main[slot] = name; shield[slot] = null; }                                        // anything else takes the whole slot
        }

        // The secondary set needs LT free. That is only true in the "Alternative 1" controller layout, where LT is the
        // radial menu. In the other two layouts LT is block, and taking the face buttons away while blocking would break dodging.
        public static bool SecondaryAvailable()
        {
            return Plugin.FavSecondary.Value && (int)ZInput.InputLayout == 1;
        }

        // ----- world input -----
        public static void WorldUpdate()
        {
            Player p = Player.m_localPlayer;
            bool on = Plugin.Enabled.Value && Plugin.FavEnabled.Value && p != null && ZInput.IsGamepadActive();
            SetHotbarHidden(on && Plugin.HideHotbar.Value);   // comes back the moment keyboard or mouse takes over
            if (!on) { Core.FavWorld = Core.FavLT = Core.FavFace = false; downTracking = false; HideHud(); return; }
            EnsureLoaded(p);
            SyncTopRow(p);
            Poll();

            // favorites stay on in build mode too, otherwise the D-pad falls back to cycling the hotbar while the hammer is out
            // With the build menu open, the D-pad and the face buttons are the build menu's. Favorites wait until it is closed.
            bool world = !InventoryGui.IsVisible() && !Hud.InRadial() && !Hud.IsPieceSelectionVisible() && !ZInput.GetButton("JoyAltKeys")
                      && M_TakeInput != null && (bool)M_TakeInput.Invoke(p, null);
            // LT + Start: the Controller Hotbar window, the quickest way to rearrange (the bag opens hidden under it)
            if (world && !TopRow && QuickChord && Core.RawDown("JoyMenu") && InventoryGui.instance != null) { Core.QuickAssign = true; Core.QuickFrame = Time.frameCount; InventoryGui.instance.Show(null, 1); }
            Core.FavWorld = world;
            int layout = (int)ZInput.InputLayout;
            Core.DownIsGP = !(layout == 1 || layout == 2);   // Classic: D-pad down is the Forsaken power. Other layouts: sit.
            Core.DownOwned = main[Down] != null;
            PadRadialTick(); PadRadialReport();

            // The secondary set also works with the hammer out, even though LT rotates the piece there too: it is the
            // way to put the hammer away again.
            bool secondary = world && SecondaryAvailable();
            bool ltHeld = secondary && ZInput.GetButton("JoyLTrigger");
            Core.FavLT = secondary;     // LT no longer opens the radial or stows the weapon
            Core.FavFace = ltHeld;      // while LT is held the face buttons belong to the secondary set
            // the D-pad set does the actions this frame: with LT held, or (swapped) without it
            /* 1.0.1 (Rob): with the LT + D-pad layer off, LT leaves the D-pad as it is: the D-pad alone's set, held or not */
            bool layer = Plugin.LtDpadLayer.Value;
            PadActions = world && (Plugin.SwapDpadActions.Value ? !(ltHeld && layer) : (ltHeld && layer));
            if (PadActions) Core.DownOwned = true;
            bool ltAny = SecondaryAvailable() && ZInput.GetButton("JoyLTrigger") && layer;
            PadShows = Plugin.SwapDpadActions.Value ? !ltAny : ltAny;
            if (Core.RawHeld("JoyLTrigger") && !ltLogged) { ltLogged = true; Plugin.Log.LogInfo("D-pad actions: LT held, world " + world + ", LT set free " + secondary + ", layout " + (int)ZInput.InputLayout + ", actions on " + PadActions + ", swapped " + Plugin.SwapDpadActions.Value); }   /* the game's own D-pad down (sit, or the Forsaken power) stays away */

            if (world)
            {
                Shots.Want((ltHeld ? "world-lt-" : "world-") + Plugin.HudPreset.Value);   // one per HUD preset, so each layout can be checked
                if (PadActions)
                {
                    downTracking = false;
                    for (int d = 0; d < 4; d++) if (padDown[d]) DoAction(p, d, ltHeld);
                }
                else
                {
                if (padDown[Up]) Use(p, Up);
                if (padDown[Left]) Use(p, Left);
                if (padDown[Right]) Use(p, Right);
                if (Core.DownOwned)
                {
                    // tap uses the favorite; a hold lets the vanilla D-pad down action through once
                    bool held = padHeld[Down];
                    if (padDown[Down]) { downTracking = true; downFired = false; downStart = Time.unscaledTime; }
                    if (downTracking)
                    {
                        if (held && !downFired && Time.unscaledTime - downStart >= Plugin.FavDownHold.Value) { downFired = true; Core.Inject(); }
                        if (!held) { if (!downFired) Use(p, Down); downTracking = false; }
                    }
                }
                }
                if (ltHeld) for (int i = 0; i < 4; i++) if (faceDown[i]) Use(p, 4 + i);
            }
            else downTracking = false;

            // The face-button set is always on screen when the layout has one. Whether it can be used right now is a
            // separate matter, shown by the disabled look. (It used to be drawn only while it could be used, so it
            // vanished whenever the buttons were taken: with the build menu open, and while guarding.)
            worldOn = world;
            UpdateHud(p, SecondaryAvailable(), ltHeld);
        }

        // Is any favorite, in either set, still on its way in or out?
        static bool AnyQueued(Player p)
        {
            for (int i = 0; i < Slots; i++)
            {
                ItemDrop.ItemData m = Find(p, main[i]), s = Find(p, shield[i]);
                if ((m != null && p.IsEquipActionQueued(m)) || (s != null && p.IsEquipActionQueued(s))) return true;
            }
            return false;
        }
        static bool anyQueued;   // as of this frame's HUD update, so both sets step back together

        // The moments when the game will not let an item be equipped or used from here: sprinting, dodging, mid-swing,
        // and while the build menu has the buttons. The cluster shows one and the same disabled look for all of them.
        static bool Disabled(Player p)
        {
            return !worldOn || (Plugin.FavDimSprint.Value && p.IsRunning()) || p.InDodge() || p.InAttack() || Hud.IsPieceSelectionVisible();
        }
        static bool worldOn;   // the favorites are taking input right now (not guarding, not in a menu, the build menu shut)
        static bool Blocked(Player p) { return Disabled(p) || AnyQueued(p); }

        // ----- the D-pad actions -----
        // Up opens the radial's Consumables group, left its Emotes, down equips the hammer (or puts it away), right sits (or
        // stands). Each is the game's own: the radial opened at that group, its sit emote, the hammer's own equip.
        public static bool PadActions; static bool ltLogged;
        // the D-pad squares show the actions: as PadActions, but also while a wheel or menu has the buttons (0.87.3: swapped,
        // the items came back the moment a wheel opened)
        static bool PadShows;
        static readonly MethodInfo M_StopEmote = AccessTools.Method(typeof(Character), "StopEmote");
        static int padRadialUpFrame = -1;
        static float padOpenedAt = -1f; static int padOpenCount;
        static readonly MethodInfo M_RadialReset = AccessTools.Method(typeof(Valheim.UI.RadialBase), "ResetAllVariables");
        static readonly FieldInfo F_RadialAnim = AccessTools.Field(typeof(Valheim.UI.RadialBase), "m_animationManager");
        static readonly MethodInfo M_AnimEndAll = F_RadialAnim != null ? AccessTools.Method(F_RadialAnim.FieldType, "EndAll") : null;
        // once, a second after a wheel opened from the D-pad: how the wheel stands, to the log (0.87.2: it took no input)
        static readonly FieldInfo F_RadialElements = AccessTools.Field(typeof(Valheim.UI.RadialBase), "m_elements");
        static string WheelItems(Valheim.UI.RadialBase r)
        {
            object els = F_RadialElements != null ? F_RadialElements.GetValue(r) : null;
            PropertyInfo cnt = els != null ? els.GetType().GetProperty("Count") : null;
            string n = cnt != null ? cnt.GetValue(els, null).ToString() : "?";
            int shown = 0; foreach (Valheim.UI.RadialMenuElement e in r.GetComponentsInChildren<Valheim.UI.RadialMenuElement>(false)) { CanvasGroup g = e.GetComponent<CanvasGroup>(); if (g == null || g.alpha > 0.05f) shown++; }
            return n + " (" + shown + " showing)";
        }
        static void PadRadialReport()
        {
            if (padOpenedAt < 0f || Time.unscaledTime - padOpenedAt < 1f) return;
            padOpenedAt = -1f;
            try
            {
                Valheim.UI.RadialBase r = Hud.instance != null ? Hud.instance.m_radialMenu : null;
                if (r == null) return;
                Vector2 dir = r.GetControllerDirection != null ? r.GetControllerDirection() : Vector2.zero;
                Plugin.Log.LogInfo("D-pad wheel after 1 s: active " + r.Active + ", blocking input " + r.IsBlockingInput + ", config " + (r.CurrentConfig != null ? r.CurrentConfig.GetType().Name : "none")
                    + ", stick read " + (r.GetControllerDirection != null) + " " + dir + ", close read " + (r.GetClose != null) + ", confirm read " + (r.GetConfirm != null) + ", enabled " + r.enabled + ", in radial " + Hud.InRadial() + ", open no. " + padOpenCount + ", items " + WheelItems(r));
            }
            catch (Exception e) { Plugin.Log.LogWarning("D-pad wheel report: " + e.Message); }
        }
        // The radial's data is only set up when the radial itself first wakes (its initializer sits on it). In PRO, LT never
        // opens the radial, so it may never have: it is set up here the same way, from the game's own data object.
        static bool soLogged;
        static Valheim.UI.RadialDataSO SO()
        {
            if (Valheim.UI.RadialData.SO != null) return Valheim.UI.RadialData.SO;
            Valheim.UI.RadialDataSO found = null;
            foreach (Valheim.UI.RadialDataSO d in Resources.FindObjectsOfTypeAll<Valheim.UI.RadialDataSO>()) { found = d; break; }
            if (found != null) Valheim.UI.RadialData.Init(found);
            if (!soLogged) { soLogged = true; Plugin.Log.LogInfo("D-pad actions: the radial's data " + (found != null ? "set up from '" + found.name + "'" : "not found")); }
            return Valheim.UI.RadialData.SO;
        }
        static void PadRadialTick()
        {
            if (!Core.PadRadialLt) return;
            /* one frame after LT is let go: the frame it is let go on still reads as its release */
            if (Core.RawHeld("JoyLTrigger")) padRadialUpFrame = -1;
            else if (padRadialUpFrame < 0) padRadialUpFrame = Time.frameCount;
            else if (Time.frameCount - padRadialUpFrame >= 2) { Core.PadRadialLt = false; padRadialUpFrame = -1; }
        }
        static void DoAction(Player p, int dir, bool lt)
        {
            if (Disabled(p)) { Square dq = squares[dir]; if (dq != null) dq.denyStart = Time.time; return; }
            try
            {
                /* the action happens at once, so its slot blinks white on the press (0.89.10, Rob); a refused one flashes red instead */
                Square fq = squares[dir]; if (fq != null) fq.flashStart = Time.time;
                if (dir == Right)
                {
                    if (p.InEmote() && p.IsSitting()) { if (M_StopEmote != null) M_StopEmote.Invoke(p, null); }
                    else p.StartEmote("sit", false);
                }
                else if (dir == Down)
                {
                    ItemDrop.ItemData h = p.GetInventory().GetItem("$item_hammer");
                    if (h == null) { Square hq = squares[dir]; if (hq != null) { hq.denyStart = Time.time; hq.flashStart = -10f; } return; }
                    p.UseItem(p.GetInventory(), h, false);   /* the game's own equip: out, or away again */
                }
                else
                {
                    Valheim.UI.RadialBase r = Hud.instance != null ? Hud.instance.m_radialMenu : null;
                    Valheim.UI.RadialDataSO so = SO();
                    if (r == null || so == null) { Plugin.Log.LogWarning("D-pad action " + dir + ": no radial (" + (r == null ? "menu" : "data") + " missing)"); return; }
                    if (!r.gameObject.activeSelf) r.gameObject.SetActive(true);
                    Valheim.UI.IRadialConfig cfg;
                    if (dir == Left) cfg = so.EmoteGroupConfig;
                    else { Valheim.UI.ItemGroupConfig c = UnityEngine.Object.Instantiate(so.ItemGroupConfig); c.GroupName = "consumables"; cfg = c; }
                    /* The game's opener's own setup (what 0.87.1 missed: without it the wheel showed but took no input), then the
                       group opened directly with nothing to go back to (0.87.3). 0.87.2 opened the main wheel first and stepped
                       into the group: it worked once, came up empty the next time, and Back led to the main wheel. */
                    if (r.OnInteractionDelay == null) r.OnInteractionDelay = delegate(float d) { PlayerController.SetTakeInputDelay(d); };
                    r.ShouldAnimateIn = true;
                    /* the wheel's controls (stick, pick, close) and its fresh state: the game only loads these when it opens the
                       main wheel, so a group opened on its own had none and could not be moved, used or closed (0.87.6) */
                    /* and any animation left over from the last close stopped first, as the game's own open does: left running, the
                       last close's fades took the new wheel's items with them, so it came up empty from the second time on (0.87.8) */
                    object anim = F_RadialAnim != null ? F_RadialAnim.GetValue(r) : null;
                    if (anim != null && M_AnimEndAll != null) M_AnimEndAll.Invoke(anim, null);
                    if (!r.Active)
                    {
                        Valheim.UI.RadialConfigHelper.SetXYControls(r);
                        Valheim.UI.RadialConfigHelper.SetItemInteractionControls(r);
                        if (M_RadialReset != null) M_RadialReset.Invoke(r, null);
                    }
                    /* the game only lets the wheel open on the frame its own button allows it (it switches this back off after a
                       close), so the second opening was refused and the wheel came up empty (0.87.8 log: 1 item, none shown) */
                    bool couldOpen = r.CanOpen;
                    r.CanOpen = true;
                    r.Open(cfg, null);
                    Plugin.Log.LogInfo("D-pad action " + dir + ": could open before " + couldOpen + ", config now " + (r.CurrentConfig != null ? r.CurrentConfig.GetType().Name : "none"));
                    padOpenedAt = Time.unscaledTime;
                    padOpenCount++;
                    Plugin.Log.LogInfo("D-pad action " + dir + ": radial opened, showing " + r.Active);
                    if (lt) { Core.PadRadialLt = true; padRadialUpFrame = -1; }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("D-pad action " + dir + ": " + e.Message); }
        }
        // the four actions' pictures: the radial's own Consumables and Emotes icons, the sit emote's, the hammer's
        static readonly Sprite[] actionIcons = new Sprite[4]; static bool actionIconsLooked;
        public static Sprite DemoActionIcon(int dir) { return ActionIcon(dir); }   /* for the splash's showcase (0.92.16) */
        static Sprite ActionIcon(int dir)
        {
            if (!actionIconsLooked && ObjectDB.instance != null && SO() != null)
            {
                actionIconsLooked = true;
                Valheim.UI.RadialDataSO so = SO();
                try
                {
                    Valheim.UI.ItemGroupMapping cm = so.ItemGroupMappings.GetMapping("consumables");
                    actionIcons[Up] = cm.Sprite;
                    actionIcons[Left] = so.EmoteGroupConfig.Sprite;
                    GameObject hp = ObjectDB.instance.GetItemPrefab("Hammer");
                    ItemDrop hd = hp != null ? hp.GetComponent<ItemDrop>() : null;
                    if (hd != null) actionIcons[Down] = hd.m_itemData.GetIcon();
                    actionIcons[Right] = so.EmoteMappings.GetMapping((Emotes)Enum.Parse(typeof(Emotes), "Sit", true)).Sprite;
                }
                catch (Exception e) { Plugin.Log.LogWarning("D-pad action pictures: " + e.Message); }
                if (actionIcons[Right] == null) actionIcons[Right] = actionIcons[Left];
                Plugin.Log.LogInfo("D-pad action pictures: " + (actionIcons[Up] != null) + " " + (actionIcons[Down] != null) + " " + (actionIcons[Left] != null) + " " + (actionIcons[Right] != null));
            }
            return actionIcons[dir];
        }

        static void Use(Player p, int slot)
        {
            ItemDrop.ItemData m = Find(p, main[slot]), s = Find(p, shield[slot]);
            if (m == null && s == null) return;                                   // greyed out: nothing to do
            // Disabled (sprinting, or another favorite is still on its way in or out): the press does nothing but
            // flash the square red. Passing it to the game here is what made the cluster flicker.
            if (Blocked(p))
            {
                Square q = slot < 4 ? squares[slot] : squares2[slot - 4];
                if (q != null) q.denyStart = Time.time;
                return;
            }
            Inventory inv = p.GetInventory();
            if (m != null && !m.IsEquipable())   // food, mead: used at once, so the slot blinks on the press itself (0.89.10, Rob)
            {
                Square uq = slot < 4 ? squares[slot] : squares2[slot - 4];
                if (uq != null) uq.flashStart = Time.time;
                p.UseItem(inv, m, false);
                return;
            }
            bool mOn = m != null && p.IsItemEquiped(m), sOn = s != null && p.IsItemEquiped(s);
            if ((m == null || mOn) && (s == null || sOn))
            {
                if (mOn) p.UseItem(inv, m, false);                                // already out: stow
                if (sOn) p.UseItem(inv, s, false);
            }
            else
            {
                if (m != null && !mOn) p.UseItem(inv, m, false);
                if (s != null && !sOn) p.UseItem(inv, s, false);
            }
        }

        // ----- squares: one per slot, laid out as a D-pad shape -----
        class Square
        {
            public Image border, halo, icon, icon2, fill, flash, ammoIcon, bg, tint, back, heldGlow, heldLine; public TMP_Text count, corner; public CanvasGroup group; public GameObject ammo;
            public Color fillColor; public bool wasQueued, draining, busy; public float animStart, animDur, flashStart = -10f, denyStart = -10f, denyK; public Vector2 home; public bool equipped; public Sprite eqSprite; public Material eqMaterial; public int lastCount = -1, lastFrame = -10; public string lastName;
        }
        const float SquareSize = 54f, AmmoGap = 4f, HaloGap = 2f, HaloLine = 2f;
        static readonly Vector2[] Offset = { new Vector2(0f, 64f), new Vector2(0f, -64f), new Vector2(-64f, 0f), new Vector2(64f, 0f) };   // 64 (was 58): room for the LT frames, so neighbours' frames never touch
        // Missing: an item that is not in the bag, greyed (the same grey the bag gives an item that cannot go on an object;
        // it was a faint white before 0.61.0, which read as see-through rather than unavailable).
        static readonly Color BorderIdle = new Color(1f, 1f, 1f, 0.28f), Missing = new Color(0.5f, 0.5f, 0.5f, 0.55f), DpadIdle = new Color(1f, 1f, 1f, 0.5f);
        // a square's inner box: black at 60%; while LT is held the LT set's boxes get a white layer at 8% over that black
        // (Rob, 2026-10-31; until 0.72.0 the black itself turned white, and items lost contrast on bright ground)
        static readonly Color BoxRest = new Color(0f, 0f, 0f, 0.5f), BoxHeld = new Color(1f, 1f, 1f, 0.08f);   // a slot's box: black at 50% (60% until 0.82.1)
        // LT held (Rob's pen.dev frame, 0.82.1): the backing plate's margin, corner radius and darkness; the square keeps its black
        // LT held (Rob's pen.dev frame, 0.89.3): on a 123 box a 2-wide white line at 75% and a white inner shadow of blur 15,
        // scaled to the slot's 54 (the line kept at 1 so it stays crisp)
        const float HeldLine = 1f,   /* 0.89.15 (Rob): 1 again, now smooth and on whole pixels */ HeldLineAlpha = 0.125f, HeldGlowBlur = 9f, HeldGlowAlpha = 0.05f;   /* 0.89.14: line 12.5%, glow 5%; 0.89.12: glow 10%; 0.89.11 (Rob): halved again (50% and 40% in 0.89.8; 75% and 60% before) */   /* 0.89.6 (Rob): line 1.5 (2 in 0.89.4-0.89.5 read too thick); 0.89.4: thicker line, wider and fainter glow (was 1, 6.6, 100%) */
        const float LtPad = 3f, LtBackRadius = 6f, LtBackAlpha = 0.65f;   /* the plate's radius 6 (Rob, 0.82.6; the slot keeps 4) */   /* 65% (50% until 0.82.5: hard to see on dark ground) */   // radius 7: concentric with the slot's 4, so the plate is 3 thick all round (8 until 0.82.4)   // 3: Rob's 6 on a 123 box, at the slot's 54 (6 in 0.82.1 read twice as thick)
        static Image hotbarEquipped;
        static bool haloLogged, fillLogged;
        static Sprite queuedSprite;
        static Color queuedColor = new Color(1f, 0.82f, 0.26f, 0.55f);
        static bool queuedLooked;
        // The inventory's own "being equipped" fill, taken from a bag slot, so the favorites use exactly that yellow.
        static void Queued()
        {
            if (queuedLooked) return;
            queuedLooked = true;
            queuedSprite = Hints.Plate();   // rounded like the square, so the yellow never shows outside the frame
            try
            {
                InventoryGui g = InventoryGui.instance;
                GameObject prefab = g != null && g.m_playerGrid != null ? g.m_playerGrid.m_elementPrefab : null;
                InventoryElement el = prefab != null ? prefab.GetComponent<InventoryElement>() : null;
                if (el != null && el.m_queued != null)
                {
                    queuedColor = el.m_queued.color;
                    if (queuedColor.a < 0.35f) queuedColor.a = 0.55f;
                    Plugin.Log.LogInfo("Favorites: the inventory's equipping fill is " + el.m_queued.color + (el.m_queued.sprite != null ? ", sprite " + el.m_queued.sprite.name : ", no sprite"));
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Favorites equipping colour: " + e.Message); }
        }
        static bool hotbarLooked;

        // The equipped highlight is the hotbar's own, so an active favorite reads the same as an equipped hotbar slot.
        static void EquippedArt(Image fill)
        {
            if (!hotbarLooked)
            {
                hotbarLooked = true;
                try
                {
                    HotkeyBar bar = Hud.instance != null ? Hud.instance.GetComponentInChildren<HotkeyBar>(true) : null;
                    Transform eq = bar != null && bar.m_elementPrefab != null ? bar.m_elementPrefab.transform.Find("equiped") : null;
                    if (eq != null) hotbarEquipped = eq.GetComponent<Image>() ?? eq.GetComponentInChildren<Image>(true);
                }
                catch (Exception e) { Plugin.Log.LogWarning("Favorites highlight: " + e.Message); }
            }
            if (hotbarEquipped != null) CopyArt(hotbarEquipped, fill);
        }

        static void CopyArt(Image from, Image to)
        {
            to.sprite = from.sprite; to.type = from.type; to.material = from.material; to.color = from.color;
            to.pixelsPerUnitMultiplier = from.pixelsPerUnitMultiplier;
        }

        // corners: when given, each square gets that button's glyph in its top-left corner (used by the face-button set)
        static void BuildSquares(Transform parent, Square[] into, TMP_Text like, bool corners)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f), topLeft = new Vector2(0f, 1f);
            for (int i = 0; i < 4; i++)
            {
                Square q = new Square();
                GameObject sq = Hints.New("slot" + i, parent);
                Hints.Place(sq, mid, mid, mid, Offset[i], new Vector2(54f, 54f));
                q.home = Offset[i];
                q.bg = Hints.Img(sq, Hints.Plate(), BoxRest);
                // the LT white, laid over the black box (not in its place), so items keep their contrast on bright ground
                // LT held (Rob's pen.dev frame, 0.82.0): a dark plate 4 larger all round comes up behind the square, and the square
                // itself turns white at 20% instead of its black; no outline
                q.back = Hints.Img(Hints.New("ltBack", sq.transform), Hints.Plate((int)(SquareSize + 2f * LtPad), LtBackRadius), new Color(0f, 0f, 0f, 0f));
                Hints.Place(q.back.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, new Vector2(2f * LtPad, 2f * LtPad));
                q.tint = Hints.Img(Hints.New("ltTint", sq.transform), Hints.Plate(), new Color(1f, 1f, 1f, 0f));
                Hints.Place(q.tint.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                /* LT held (Rob's pen.dev frame, 0.89.3): a soft white inner glow and a white inner line on the slot, under the item */
                q.heldGlow = Hints.Img(Hints.New("ltGlowIn", sq.transform), Hints.InnerGlow(SquareSize, HeldGlowBlur), new Color(1f, 1f, 1f, 0f));
                Hints.Place(q.heldGlow.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                q.heldGlow.raycastTarget = false;
                q.heldLine = Hints.Img(Hints.New("ltLineIn", sq.transform), Hints.InnerLine(SquareSize, HeldLine), new Color(1f, 1f, 1f, 0f));
                Hints.Place(q.heldLine.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                q.heldLine.raycastTarget = false;
                q.fill =Hints.Img(Hints.New("equipped", sq.transform), null, new Color(0.35f, 0.6f, 1f, 0.55f));
                Hints.Place(q.fill.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                EquippedArt(q.fill);
                // The game's blue, on the square's own rounded shape (corner radius 6): the hotbar's art has corners of its
                // own, drawn at half size by the canvas, which did not match the square or its LT frame.
                q.fill.sprite = Hints.Plate(); q.fill.material = null; q.fill.pixelsPerUnitMultiplier = 1f;
                // the game's colour is kept; if its blue lived in its art (a white tint), the hotbar's blue is used instead
                Color fc = q.fill.color;
                if (fc.r > 0.9f && fc.g > 0.9f && fc.b > 0.9f) q.fill.color = new Color(96f / 255f, 168f / 255f, 229f / 255f, 0.76f);
                if (!fillLogged) { fillLogged = true; Plugin.Log.LogInfo("Favorites: equipped fill on the square's shape, colour " + q.fill.color + " (the game's art was " + (hotbarEquipped != null && hotbarEquipped.sprite != null ? hotbarEquipped.sprite.name : "not found") + ", tint " + fc + ")"); }
                q.fillColor = q.fill.color; q.eqSprite = q.fill.sprite; q.eqMaterial = q.fill.material;
                q.fill.type = Image.Type.Filled; q.fill.fillMethod = Image.FillMethod.Vertical; q.fill.fillOrigin = (int)Image.OriginVertical.Bottom;
                q.fill.enabled = false;
                q.group = sq.AddComponent<CanvasGroup>();
                q.group.blocksRaycasts = false;
                q.icon = Hints.Img(Hints.New("icon", sq.transform), null, Color.white);
                q.icon.preserveAspect = true;
                Hints.Place(q.icon.gameObject, mid, mid, mid, Vector2.zero, new Vector2(46f, 46f));
                q.icon2 = Hints.Img(Hints.New("icon2", sq.transform), null, Color.white);
                q.icon2.preserveAspect = true;
                Hints.Place(q.icon2.gameObject, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-1f, 1f), new Vector2(30f, 30f));
                // the white flash that marks "now equipped": over the icon, under the border and the count
                q.flash = Hints.Img(Hints.New("flash", sq.transform), Hints.Plate(), Color.clear);
                Hints.Place(q.flash.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                q.flash.enabled = false;
                q.border = Hints.Img(Hints.New("border", sq.transform), Hints.Box(), BorderIdle);
                q.border.type = Image.Type.Sliced;
                Hints.Place(q.border.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                // The LT frame: a 2-wide line standing 2 clear of the square all round, a frame of its own rather than
                // the square's edge. Only shown while LT is held (the face-button set).
                q.halo = Hints.Img(Hints.New("ltFrame", sq.transform), null, Color.clear);
                // The line keeps the width it had as stretchable art: the canvas drew that art's 2-wide line scaled by its
                // reference pixels per unit, so it came out thinner than 2. Only the corners change (concentric now).
                Canvas cv = q.halo.canvas;
                float lineScale = cv != null && cv.referencePixelsPerUnit > 0f ? cv.referencePixelsPerUnit / 100f : 1f;
                if (!haloLogged) { haloLogged = true; Plugin.Log.LogInfo("LT frame: the canvas draws stretchable art at " + lineScale.ToString("0.###") + " of its size; the frame's line is " + (HaloLine * lineScale).ToString("0.##") + " wide"); }
                float line = HaloLine * lineScale;
                q.halo.sprite = Hints.Frame(SquareSize, HaloGap, line);   // corners concentric with the square's
                q.halo.raycastTarget = false;   // shown at exactly the size it was drawn for
                Hints.Place(q.halo.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, new Vector2(2f * (HaloGap + line), 2f * (HaloGap + line)));
                q.count = Hints.Text(Hints.New("count", sq.transform), like, 13f, Color.white, TextAlignmentOptions.Right);
                Hints.Place(q.count.gameObject, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-4f, 2f), new Vector2(0f, 16f));
                if (corners)
                {
                    q.corner = Hints.Text(Hints.New("button", sq.transform), like, 11f, Color.white, TextAlignmentOptions.Center);
                    Hints.Place(q.corner.gameObject, topLeft, topLeft, mid, new Vector2(3f, -3f), new Vector2(18f, 18f));
                }
                // The ammo a weapon is using (the arrow for a bow) sits in a small box above the square. Only the
                // bottom square puts it below, where there is room.
                // No box of its own: just the ammo's picture, as wide as the square, 4 away from it.
                q.ammo = Hints.New("ammo", sq.transform);
                Hints.Place(q.ammo, mid, mid, mid, new Vector2(0f, i == Down ? -(SquareSize + AmmoGap) : SquareSize + AmmoGap), new Vector2(SquareSize, SquareSize));
                q.ammoIcon = Hints.Img(q.ammo, null, Color.white);
                q.ammoIcon.preserveAspect = true;
                q.ammo.SetActive(false);
                into[i] = q;
            }
        }

        // The face buttons as a small diamond, each in the position it has on the pad. It sits in the middle of the
        // secondary set, the way the D-pad icon sits in the middle of the primary one.
        static TMP_Text[] BuildFaceDiamond(Transform parent, TMP_Text like)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f);
            // The four glyphs sit in the middle of the set as a tight diamond, laid out as the buttons are on the pad,
            // nearly touching: Y above, A below, X left, B right.
            Vector2[] at = { new Vector2(0f, DiamondAt), new Vector2(0f, -DiamondAt), new Vector2(-DiamondAt, 0f), new Vector2(DiamondAt, 0f) };   // Y, A, X, B
            TMP_Text[] glyphs = new TMP_Text[4];
            for (int i = 0; i < 4; i++)
            {
                glyphs[i] = Hints.Text(Hints.New("face" + i, parent), like, DiamondSize, Color.white, TextAlignmentOptions.Center);
                Hints.Place(glyphs[i].gameObject, mid, mid, mid, at[i], new Vector2(30f, 30f));
            }
            return glyphs;
        }
        // the glyph's picture takes the text's colour (by default the game's glyphs only take its fade), so it can be darkened
        static string Tinted(string g) { return string.IsNullOrEmpty(g) || g.Contains("tint=") ? g : System.Text.RegularExpressions.Regex.Replace(g, "(<sprite[^>]*)>", "$1 tint=1>"); }
        static void FaceDiamond(TMP_Text[] glyphs)
        {
            if (glyphs == null) return;
            for (int i = 0; i < 4; i++) { string g = Tinted(Hints.Glyph(Face[i])); if (glyphs[i].text != g) glyphs[i].text = g; }
        }

        // For a weapon that uses ammo: the ammo it is using now, or failing that the first matching ammo in the bag.
        static ItemDrop.ItemData AmmoFor(Player p, ItemDrop.ItemData weapon)
        {
            if (weapon == null || string.IsNullOrEmpty(weapon.m_shared.m_ammoType)) return null;
            ItemDrop.ItemData.ItemType t = weapon.m_shared.m_itemType;
            if (t == ItemDrop.ItemData.ItemType.Ammo || t == ItemDrop.ItemData.ItemType.AmmoNonEquipable) return null;   // ammo names its own type too
            // Meads and other stackable consumables name an "ammo type" too; they are not weapons and have no ammo.
            if (t == ItemDrop.ItemData.ItemType.Consumable || weapon.m_shared.m_maxStackSize > 1) return null;
            ItemDrop.ItemData a = p.GetAmmoItem();
            if (a == null || a.m_shared.m_ammoType != weapon.m_shared.m_ammoType || !p.GetInventory().ContainsItem(a))
                a = p.GetInventory().GetAmmoItem(weapon.m_shared.m_ammoType, null);
            // only real ammo shows: arrows, bolts, bait
            if (a != null && a.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Ammo && a.m_shared.m_itemType != ItemDrop.ItemData.ItemType.AmmoNonEquipable) return null;
            return a;
        }

        // Where the drawing sits inside an item's picture, as fractions of the picture (0 to 1, from the bottom-left).
        // Worked out once per picture by reading its pixels.
        static readonly System.Collections.Generic.Dictionary<Sprite, Rect> inkOf = new System.Collections.Generic.Dictionary<Sprite, Rect>();
        static Rect Ink(Sprite s)
        {
            Rect r = new Rect(0.1f, 0.1f, 0.8f, 0.8f);   // a fair guess if the picture cannot be read
            if (s == null) return r;
            Rect known;
            if (inkOf.TryGetValue(s, out known)) return known;
            try
            {
                Texture tex = s.texture;
                Rect tr = s.textureRect;
                Vector2 off = s.textureRectOffset;
                int w = Mathf.Max(1, Mathf.RoundToInt(tr.width)), h = Mathf.Max(1, Mathf.RoundToInt(tr.height));
                RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                RenderTexture prev = RenderTexture.active;
                Graphics.Blit(tex, rt, new Vector2(tr.width / tex.width, tr.height / tex.height), new Vector2(tr.x / tex.width, tr.y / tex.height));
                RenderTexture.active = rt;
                Texture2D copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Color32[] px = copy.GetPixels32();
                UnityEngine.Object.Destroy(copy);
                int x0 = w, x1 = -1, y0 = h, y1 = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (px[y * w + x].a > 40) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
                float fw = Mathf.Max(1f, s.rect.width), fh = Mathf.Max(1f, s.rect.height);
                if (x1 >= x0 && y1 >= y0) r = Rect.MinMaxRect((off.x + x0) / fw, (off.y + y0) / fh, (off.x + x1 + 1) / fw, (off.y + y1 + 1) / fh);
            }
            catch (Exception e) { Plugin.Log.LogInfo("Ammo picture could not be measured (" + e.Message + "), using a guess"); }
            inkOf[s] = r;
            return r;
        }

        static Sprite IconOf(ItemDrop.ItemData inBag, string name)
        {
            try
            {
                if (inBag != null) return inBag.GetIcon();
                ItemDrop.ItemData d = Prefab(name);
                return d != null ? d.GetIcon() : null;
            }
            catch (Exception) { return null; }
        }

        // first: the slot the set starts at (0 for the D-pad set, 4 for the face-button set).
        // marked: an item name whose square gets a white outline (the item being assigned), or null.
        // rest: how visible a square is when its set is not the one being held (1 for a set that is always live).
        //       A square whose item is out, or on its way out, ignores this and stays at full strength.
        // dim:  the whole cluster's disabled look (sprinting). 1 is normal.
        static void FillSquares(Square[] set, Player p, int first, string marked, float rest, float dim)
        {
            for (int i = 0; i < 4; i++)
            {
                Square q = set[i];
                int slot = first + i;
                if (set == squares && PadShows)
                {
                    /* the D-pad set does the actions right now: their pictures instead of the items, the hammer lit while it is out */
                    Sprite ai = ActionIcon(i);
                    q.icon.enabled = ai != null; q.icon.sprite = ai; q.icon.color = Color.white;
                    q.icon2.enabled = false; q.count.text = "";
                    if (q.ammo.activeSelf) q.ammo.SetActive(false);
                    ItemDrop.ItemData hm = i == Down ? p.GetInventory().GetItem("$item_hammer") : null;   /* the hammer is on down since 0.89.1 (this lit the sit slot until 0.89.9) */
                    bool hOut = hm != null && p.IsItemEquiped(hm);
                    if (q.fill.sprite != q.eqSprite) { q.fill.sprite = q.eqSprite; q.fill.material = q.eqMaterial; }
                    q.fill.enabled = hOut; q.fill.fillAmount = 1f; q.fill.color = q.fillColor;
                    float adt = (Time.time - q.denyStart) / Mathf.Max(0.05f, Plugin.FavFlash.Value);
                    bool aden = adt >= 0f && adt < 1f;
                    float aft = (Time.time - q.flashStart) / Mathf.Max(0.05f, Plugin.FavFlash.Value);
                    bool ablink = aft >= 0f && aft < 1f && Plugin.FavFlash.Value > 0f;
                    q.flash.enabled = aden || ablink;
                    if (aden) q.flash.color = new Color(0.92f, 0.16f, 0.12f, 0.85f * (1f - adt) * (1f - adt));
                    else if (ablink) q.flash.color = new Color(1f, 1f, 1f, 0.8f * (1f - aft) * (1f - aft));   /* the same white blink as an item has */
                    q.equipped = hOut; q.denyK = aden ? 1f - adt : 0f; q.busy = false; q.wasQueued = false;
                    q.border.color = Color.clear;
                    continue;
                }
                ItemDrop.ItemData m = Find(p, main[slot]), s = Find(p, shield[slot]);
                bool hasMain = main[slot] != null, hasShield = shield[slot] != null;

                q.icon.enabled = hasMain;
                if (hasMain) { q.icon.sprite = IconOf(m, main[slot]); q.icon.color = m != null ? Color.white : Missing; }
                q.icon2.enabled = hasShield;
                if (hasShield) { q.icon2.sprite = IconOf(s, shield[slot]); q.icon2.color = s != null ? Color.white : Missing; }
                if (q.corner != null) { string g = Hints.Glyph(Face[i]); if (q.corner.text != g) q.corner.text = g; }

                string count = "";
                if (m != null && m.m_shared.m_maxStackSize > 1)
                {
                    int n = 0;
                    foreach (ItemDrop.ItemData it in p.GetInventory().GetAllItems()) if (NameOf(it) == main[slot]) n += it.m_stack;
                    count = n.ToString();
                }
                // a weapon that uses ammo shows how much of it is left, the way food shows how many are left
                ItemDrop.ItemData ammo = AmmoFor(p, m);
                bool usesAmmo = m != null && !string.IsNullOrEmpty(m.m_shared.m_ammoType) && m.m_shared.m_maxStackSize <= 1
                             && m.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Ammo && m.m_shared.m_itemType != ItemDrop.ItemData.ItemType.AmmoNonEquipable;
                if (usesAmmo)
                {
                    int n = 0;
                    if (ammo != null) { string an = NameOf(ammo); foreach (ItemDrop.ItemData it in p.GetInventory().GetAllItems()) if (NameOf(it) == an) n += it.m_stack; }
                    count = n.ToString();
                }
                if (q.ammo.activeSelf != (ammo != null)) q.ammo.SetActive(ammo != null);
                if (ammo != null)
                {
                    // An item picture has empty space around the item (an arrow is a thin line across the middle of
                    // its picture). So the picture is placed by what is drawn in it: the drawn part ends 4 from the square.
                    Sprite sp = IconOf(ammo, null);
                    q.ammoIcon.sprite = sp;
                    Rect ink = Ink(sp);
                    float half = SquareSize * 0.5f;
                    bool below = i == Down && !(Plugin.RowHotbar && (set == squares || set == squares2));   // in the row the glyphs are below, so ammo always goes above
                    float y = below ? -(half + AmmoGap) + (1f - ink.yMax) * SquareSize - half
                                        : (half + AmmoGap) - ink.yMin * SquareSize + half;
                    float x = -((ink.xMin + ink.xMax) * 0.5f - 0.5f) * SquareSize;
                    ((RectTransform)q.ammo.transform).anchoredPosition = new Vector2(x, y);
                }
                q.count.text = count; q.count.alpha = 1f;

                bool equipped = (m != null || s != null) && (m == null || p.IsItemEquiped(m)) && (s == null || p.IsItemEquiped(s)) && (m == null || m.IsEquipable());
                bool isMarked = marked != null && (main[slot] == marked || shield[slot] == marked);
                bool picking = marked != null;   // drawn inside the assign overlay
                // No outline at rest: plain dark boxes with rounded corners, like most square elements in the game's UI.
                // The face-button set gets its LT frame, standing 2 clear of each square, only while LT is held (set in UpdateHud).
                q.border.color = Color.clear;

                // Equip transition: while the game is still drawing or stowing the item, the square fills (or drains) in a
                // half-strength blue. Only once the item is actually equipped does it switch to the full-strength blue.
                bool queued = !picking && ((m != null && p.IsEquipActionQueued(m)) || (s != null && p.IsEquipActionQueued(s)));
                if (queued && !q.wasQueued)
                {
                    q.animStart = Time.time;
                    q.draining = equipped;   // everything was out already, so this press is stowing it
                    q.animDur = 0f;
                    if (m != null && p.IsEquipActionQueued(m)) q.animDur += m.m_shared.m_equipDuration;
                    if (s != null && p.IsEquipActionQueued(s)) q.animDur += s.m_shared.m_equipDuration;
                    q.animDur = Mathf.Max(0.15f, q.animDur);
                }
                // The moment equipping finishes, the square flashes white for a split second and settles into the blue,
                // the way a cooldown lights up when it is ready. Stowing does not flash.
                if (!queued && q.wasQueued && equipped && !q.draining) q.flashStart = Time.time;
                // A consumable (food, mead) gets the same white flash the moment one is used up: its count just went
                // down. Only from one frame to the very next, so coming back from the inventory with fewer does not flash.
                if (!picking && m != null && !m.IsEquipable() && m.m_shared.m_maxStackSize > 1)
                {
                    int have;
                    if (int.TryParse(count, out have))
                    {
                        if (q.lastName == main[slot] && Time.frameCount - q.lastFrame <= 2 && q.lastCount > have && Time.time - q.flashStart > Mathf.Max(0.05f, Plugin.FavFlash.Value)) q.flashStart = Time.time;   /* not again when the press already blinked it */
                        q.lastCount = have; q.lastName = main[slot]; q.lastFrame = Time.frameCount;
                    }
                }
                else if (!picking) q.lastName = null;
                q.wasQueued = queued;
                float ft = (Time.time - q.flashStart) / Mathf.Max(0.05f, Plugin.FavFlash.Value);
                bool flashing = ft >= 0f && ft < 1f && Plugin.FavFlash.Value > 0f;
                // Pressed while it cannot be used (sprinting, or something else is still being equipped): the same
                // flash in red. It says "not yet", and takes over from the white one if both were due.
                float dt = (Time.time - q.denyStart) / Mathf.Max(0.05f, Plugin.FavFlash.Value);
                bool denying = dt >= 0f && dt < 1f;
                if (denying) flashing = true;
                if (q.flash.enabled != flashing) q.flash.enabled = flashing;
                if (denying) q.flash.color = new Color(0.92f, 0.16f, 0.12f, 0.85f * (1f - dt) * (1f - dt));
                else if (flashing) q.flash.color = new Color(1f, 1f, 1f, 0.8f * (1f - ft) * (1f - ft));   // bright at once, then eases out
                q.equipped = equipped;
                q.denyK = denying ? (1f - dt) : 0f;
                if (queued)
                {
                    // On its way in or out: the same wipe as before, in the yellow the inventory uses for an item
                    // that is being equipped, so the two read as the same thing.
                    float t = Mathf.Clamp01((Time.time - q.animStart) / q.animDur);
                    Queued();
                    if (q.fill.sprite != queuedSprite) { q.fill.sprite = queuedSprite; q.fill.material = null; }
                    q.fill.enabled = true;
                    q.fill.fillAmount = q.draining ? 1f - t : t;
                    q.fill.color = queuedColor;
                }
                else
                {
                    // In the world, blue means the item is out. In the assign overlay it means "this is the item being
                    // set" and nothing else: what happens to be equipped is not marked there.
                    if (q.fill.sprite != q.eqSprite) { q.fill.sprite = q.eqSprite; q.fill.material = q.eqMaterial; }
                    q.fill.enabled = picking ? isMarked : equipped;
                    q.fill.fillAmount = 1f;
                    q.fill.color = q.fillColor;
                }
                q.busy = queued;
            }
            for (int i = 0; i < 4; i++)
            {
                Square q = set[i];
                // There is one enabled look and one disabled look. The reasons a square steps back (its set is at rest,
                // something else is equipping, the whole cluster is disabled) do not stack: the lowest one wins, so
                // both sets end up at the same strength when the cluster is disabled.
                // the only standing difference in strength is the face-button set at rest, until LT is held
                float a = Mathf.Min(q.equipped || q.busy ? 1f : rest, dim);
                q.group.alpha = Mathf.Lerp(a, 1f, q.denyK);   // a red flash is always seen at full strength, then the square settles back
            }
        }

        static TMP_Text MainFont(Transform root)
        {
            // take the game's main text font; the first text found on the HUD happens to use the small pixel font
            TMP_Text like = null;
            foreach (TMP_Text t in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (like == null) like = t;
                if (t.font != null && t.font.name.Contains("AveriaSans")) { like = t; break; }
            }
            return like;
        }

        // ----- HUD: the primary cluster, an LT marker on its top-left corner, and the secondary cluster to its left while LT is held -----
        static GameObject hudRoot, hudSecondary;
        public static RectTransform HudBox { get { return hudRoot != null && hudRoot.activeInHierarchy ? hudRoot.transform as RectTransform : null; } }

        static float hudAlpha = 1f;
        const float SprintAlpha = 0.35f;
        const float CrossGap = 208f;                             // centre to centre, between the two sets
        // the compact cross keeps more room between its sets: each set's side glyphs stand out past its squares, and at the
        // cross's spacing the two sets' facing glyphs nearly touched (Rob, 0.79.1)
        const float CompactGap = 264f;   // follows the boxes' spacing (272 in 0.81.5, 260 before)
        static float SetGap { get { return Plugin.CompactHotbar ? CompactGap : CrossGap; } }
        static GameObject hudPrimary;
        static float ltK;   // 0 at rest, 1 while LT is held, eased
        static Image dpadIcon, pickDpadIcon;
        static TMP_Text dpadGlyph, pickDpadGlyph;
        static TMP_Text ltMarker;
        static Image ltFrame, ltTint, ltGlow;
        static Image ltOutline; static string outlineFor; static Vector2 outlineCentre, outlineSize;
        const float LtRim = 3f, LtRimAlpha = 0.5f;   // LT's outline while held: 3 out all round, black at 50%
        const float LtGlow = 0.0875f;   /* 0.89.14 (Rob): halved again; 0.89.13: halved (35% before) */   // the glow's strength at its middle while LT is held
        static readonly Square[] squares = new Square[4], squares2 = new Square[4];
        static TMP_Text[] hudFace, pickFace, hudPadGlyphs;


        // How far in from the left edge the bottom left corner is taken, in the hotbar's own space, or 0 if free: by the
        // game's health panel and Forsaken power (when the game's own resource bars are in use), or by the Horizontal resource
        // bars when they sit at the bottom left.
        const float CornerGap = 24f;
        static readonly Vector3[] tc = new Vector3[4];
        static float LeftTaken(RectTransform space)
        {
            Hud h = Hud.instance;
            if (space == null || h == null) return 0f;
            float right = float.MinValue;
            if (Plugin.VitalsBottomLeft) { RectTransform v = Vitals.Box; if (v != null && v.gameObject.activeInHierarchy) { v.GetWorldCorners(tc); right = tc[2].x; } }
            else if (!Plugin.VitalsGrouped)
            {
                foreach (RectTransform r in new RectTransform[] { h.m_healthPanel, h.m_gpRoot })
                    if (r != null && r.gameObject.activeInHierarchy) { r.GetWorldCorners(tc); right = Mathf.Max(right, Mathf.Max(tc[0].x, tc[2].x)); }
            }
            if (right == float.MinValue) return 0f;
            return space.InverseTransformPoint(new Vector3(right, 0f, 0f)).x - space.rect.xMin;
        }
        // How far in from the right edge the bottom right corner is taken by the minimap when it sits there, or 0.
        static float RightTaken(RectTransform space)
        {
            Minimap mm = Minimap.instance;
            if (space == null || mm == null || !Plugin.MinimapBottom || !MapPlace.Moved || MapPlace.Hidden) return 0f;   // a hidden map leaves the corner free
            RectTransform map = mm.m_mapImageSmall != null ? mm.m_mapImageSmall.rectTransform : null;
            if (map == null || !map.gameObject.activeInHierarchy) return 0f;
            map.GetWorldCorners(tc);
            return space.rect.xMax - space.InverseTransformPoint(new Vector3(tc[0].x, 0f, 0f)).x;
        }
        static void HideHud() { if (hudRoot != null) hudRoot.SetActive(false); ClearBars(false); }

        // The minimap's distance from the right edge of the screen, in the hotbar's space: the one edge distance every corner
        // piece of the HUD keeps (the minimap, the resource bars, the hotbar). 30 when there is no map to measure.
        static float lastEdge = 30f;
        // the map picture's distance from the side (or, vertical, from the top and so, moved down, from the bottom)
        static float lastEdgeY = 30f;
        public static float MapEdge(RectTransform space) { return MapEdge(space, false); }
        public static float MapEdge(RectTransform space, bool vertical)
        {
            float right, top;
            if (space != null && MapPlace.PicGaps(out right, out top))
            {
                float s = Mathf.Max(0.0001f, space.lossyScale.x);
                lastEdge = right / s; lastEdgeY = top / s;
            }
            return vertical ? lastEdgeY : lastEdge;
        }

        // With the hotbar at the bottom centre (the centred cross or the row), the game's stamina, eitr and adrenaline
        // bars, which also sit at the bottom centre, are moved up just enough to clear it with a gap. Moved by an offset
        // on top of wherever the game (or another mod) puts them, and handed back the moment the hotbar leaves.
        static readonly Nudge[] barNudges = { new Nudge(), new Nudge(), new Nudge() };
        // The hammer's box (the piece picked, its name and what it costs, at the bottom of the screen while a building tool
        // is held) is lifted clear of the hotbar in the same way, whichever style the hotbar has, when the two would meet.
        static readonly Nudge boxNudge = new Nudge();
        static RectTransform buildBox; static Hud boxFor;
        static void LiftBuildBox(Hud hud, float xMin, float xMax, float top, float gap)
        {
            if (boxFor != hud)
            {
                boxFor = hud; buildBox = null;
                Transform at = hud.m_buildSelection != null ? hud.m_buildSelection.transform : null;
                Transform stop = hud.m_buildHud != null ? hud.m_buildHud.transform : null;
                while (at != null && at.parent != null && at.parent != stop && at.parent != hud.transform) at = at.parent;
                buildBox = at as RectTransform;
                Plugin.Log.LogInfo("Hammer box: " + (buildBox != null ? "'" + buildBox.name + "' under '" + (buildBox.parent != null ? buildBox.parent.name : "-") + "'" : "not found"));
            }
            RectTransform b = buildBox;
            if (b == null) return;
            // Bottom HUD: the box goes to the top left corner, as far in from the edges as the minimap is from its own (0.88.4,
            // Rob). Action RPG with the minimap at the bottom: the top right corner, the same distances (0.89.2, Rob). Vanilla
            // keeps the game's own place (0.85-0.88.3 lifted it above the hotbar in every preset).
            Vector2 by = Vector2.zero;
            bool rpgCorner = Plugin.PresetActionRpg && Plugin.MinimapBottom;
            if ((Plugin.PresetBottom || rpgCorner) && Plugin.LayoutOn && b.gameObject.activeInHierarchy)
            {
                float scale = b.parent != null ? Mathf.Max(0.0001f, b.parent.lossyScale.y) : 1f;
                b.GetWorldCorners(barCorners);
                Vector2 now = boxNudge.Present(b) * scale;
                float homeLeft = barCorners[0].x - now.x, homeRight = barCorners[2].x - now.x, homeTop = barCorners[1].y - now.y;
                RectTransform space = b.parent as RectTransform;
                float edge = MapEdge(space) * scale, edgeY = MapEdge(space, true) * scale;
                float dx = rpgCorner ? (Screen.width - edge - homeRight) : (edge - homeLeft);
                by = new Vector2(dx / scale, (Screen.height - edgeY - homeTop) / scale);
            }
            boxNudge.Set(b, by);
        }
        static readonly Vector3[] barCorners = new Vector3[4];
        const float BarGap = 30f;   // between the hotbar's top and the game's bars (12 until 0.80.0: too close, Rob)
        // The lift is applied just before the screen is drawn: each bar's own animator puts it back in its place every
        // frame after Update, so a lift made in Update never showed (the stamina bar sat on the centred cross).
        static bool barsOn, barsHooked;
        static void ClearBars(bool on)
        {
            barsOn = on;
            if (!barsHooked) { barsHooked = true; Canvas.willRenderCanvases += LiftBars; }
        }
        static void LiftBars()
        {
            long pt = Perf.Now();
            try { LiftBarsNow(); } finally { Perf.Lap("stamina lift", pt); }
        }
        static void LiftBarsNow()
        {
            bool on = barsOn;
            Hud hud = Hud.instance;
            if (hud == null) return;
            RectTransform[] bars = { hud.m_staminaBar2Root, hud.m_eitrBarRoot, hud.m_adrenalineBarRoot };
            float xMin = float.MaxValue, xMax = float.MinValue, top = float.MinValue;
            /* measured whatever the hotbar's place: the hammer's piece box below is lifted clear of it wherever it is */
            if (hudRoot != null && hudRoot.activeInHierarchy)
            {
                foreach (Square[] set in new Square[][] { squares, squares2 })
                    foreach (Square q in set)
                    {
                        if (q == null || !q.border.gameObject.activeInHierarchy) continue;
                        ((RectTransform)q.border.transform.parent).GetWorldCorners(barCorners);
                        xMin = Mathf.Min(xMin, barCorners[0].x); xMax = Mathf.Max(xMax, barCorners[2].x); top = Mathf.Max(top, barCorners[2].y);
                        if (q.ammo.activeInHierarchy) { ((RectTransform)q.ammo.transform).GetWorldCorners(barCorners); top = Mathf.Max(top, barCorners[2].y); }
                    }
                // and the button glyphs that stand out past the squares (the compact cross's sit on the outer edges, so the up
                // glyph rises above the top square; until 0.79.2 the bars were lifted clear of the squares only and sat on it)
                foreach (TMP_Text[] set in new TMP_Text[][] { hudPadGlyphs, hudFace, new TMP_Text[] { ltMarker } })
                {
                    if (set == null) continue;
                    foreach (TMP_Text g in set)
                    {
                        if (g == null || !g.gameObject.activeInHierarchy || g.alpha < 0.05f || string.IsNullOrEmpty(g.text)) continue;
                        Bounds tb = g.textBounds;
                        float glyphTop;
                        if (tb.size.y > 0.5f) glyphTop = g.transform.TransformPoint(tb.max).y;
                        else { g.rectTransform.GetWorldCorners(barCorners); glyphTop = barCorners[1].y; }
                        top = Mathf.Max(top, glyphTop);
                    }
                }
            }
            // keyboard and mouse with the preset applied: the game's own hotbar at the bottom centre is what the bars clear
            float gx0, gx1, gtop;
            if (top == float.MinValue && GameHotbar.CentredRect(out gx0, out gx1, out gtop)) { xMin = gx0; xMax = gx1; top = gtop; on = true; }
            float gap = BarGap * (hudRoot != null && hudRoot.transform.parent != null ? hudRoot.transform.parent.lossyScale.y : 1f);
            LiftBuildBox(hud, xMin, xMax, top, gap);
            /* the bars only while the hotbar is at the bottom centre */
            if (!on) top = float.MinValue;
            for (int i = 0; i < bars.Length; i++)
            {
                RectTransform b = bars[i];
                if (b == null) continue;
                float lift = 0f;
                if (top > float.MinValue)
                {
                    float scale = b.parent != null ? Mathf.Max(0.0001f, b.parent.lossyScale.y) : 1f;
                    b.GetWorldCorners(barCorners);
                    // what this has moved it by, if the lift is still in place (its animator may have put it back,
                    // and counting a lift that is gone made it climb every frame, off the top of the screen)
                    float shift = barNudges[i].Present(b).y * scale;
                    float bottom = barCorners[0].y - shift;
                    // always above it, however narrow the bar is right now: a bar in the gap between the two sets would
                    // run into them as it grows (eitr starts narrow and widens as it fills up)
                    if (bottom < top + gap) lift = (top + gap - bottom) / scale;
                }
                barNudges[i].Set(b, new Vector2(0f, lift));
            }
        }

        // Row: the four D-pad slots, then LT, then the four LT slots, at the bottom centre of the screen. In each group
        // the slots run left to right as the buttons sit: left, up, down, right on the D-pad; X, Y, A, B on the face.
        // Each slot's button glyph sits under it. The cross keeps its own layout exactly as it was.
        const float DiamondAt = 15.5f, DiamondSize = 14f;   // the cross's face glyphs: a tight diamond in the middle, a small gap between neighbours (19 overlapped)
        const float RowPitch = 64f, RowGroupGap = 80f,   /* between the row's two groups, LT in it: 80 (56 until 0.82.3) */ RowFromBottom = 100f, RowGlyphGap = 19f;   /* 0.89.16 (Rob): the glyphs a little lower, clear of the boxes (14 before) */
        static readonly float[] RowX = { -0.5f, 0.5f, -1.5f, 1.5f };   // by slot: up, down, left, right (and Y, A, X, B)
        static float RowHalf { get { return 1.5f * RowPitch + SquareSize * 0.5f + RowGroupGap * 0.5f; } }
        static string PadGlyphName(int i) { return Pad[i]; }
        // Compact cross: the cross's sets drawn tight. Up and down stand close together (10 apart) between left and right,
        // which keep the cross's places; each slot's button sits on its outer edge, half on the square (Rob's wireframe).
        // 16 between neighbouring boxes both ways (10 until 0.81.5: a touch more room, Rob)
        const float CompactY = 33f, CompactX = 66f;   // 12 between neighbouring boxes (16 in 0.81.5, 10 before)
        static Vector2 CompactAt(int i) { return i == Up ? new Vector2(0f, CompactY) : i == Down ? new Vector2(0f, -CompactY) : i == Left ? new Vector2(-CompactX, 0f) : new Vector2(CompactX, 0f); }
        // each slot's button on its top right corner, half on the square (0.90.6, Rob; on the outer edge before)
        static Vector2 CompactCorner(int i) { float k = SquareSize * 0.5f - CornerIn; return CompactAt(i) + new Vector2(k, k); }
        const float CornerIn = 6f;   /* 0.90.7 (Rob): 6 in from the corner both ways, mostly on the slot (right on the corner read too far out) */
        static Vector2 CompactEdge(int i) { Vector2 at = CompactAt(i); float h = SquareSize * 0.5f; return at + (i == Up ? new Vector2(0f, h) : i == Down ? new Vector2(0f, -h) : i == Left ? new Vector2(-h, 0f) : new Vector2(h, 0f)); }
        // Where LT sits (in the face-button set's own space). In the row and the compact cross: at the bottom, midway between
        // the two sets, on the line of the slots' own button glyphs (Rob, 0.81.0; before, small on the first slot's corner).
        // In the cross: in the middle of its set, as always.
        // LT between the two sets (the row always; the crosses unless the archived middle look is on)
        static bool LtBetween(bool row) { return row || !Plugin.LtInSetMiddle.Value; }
        static Vector2 LtAt(bool row, bool compact)
        {
            // the row: midway between the sets, level with the slots' middles (0.81.3; on the glyph line before)
            if (row) return new Vector2(-RowHalf, 0f);
            // both crosses since 0.90.0 (Rob): as in the row, midway between the two sets, level with the bottom slots' middles
            if (!Plugin.LtInSetMiddle.Value) return new Vector2(-SetGap * 0.5f, compact ? -CompactY : Offset[Down].y);
            // the compact cross mirrors the cross (0.81.4): LT in the middle of its set, fading out while held; its button glyphs
            // stay on the squares' outer edges (0.81.3 tucked LT under X, beside A)
            return Vector2.zero;
        }
        const float LtBox = 40f;   // fits the larger LT (34 until 0.82.3)
        const float FaceDark = 0.35f;   /* the face glyphs' brightness while LT is not held: darkened to 35%, fully opaque (0.91.16) */
        // LT's outline while held (the row): one image of LT's own glyph grown evenly by LtRim all round, black at 50%. Made
        // from the glyph's own pixels (whichever controller's), so the rim is the same thickness everywhere and the same
        // shade (copies laid over each other came out uneven or had to be solid black, 0.82.7-0.82.10). Made again when the
        // glyph or its size changes; it follows LT's press.
        static void LtOutline(bool row, bool compact)
        {
            if (ltOutline == null) return;
            float k = LtBetween(row) ? ltK * hudAlpha : 0f;
            if (k <= 0.001f) { if (ltOutline.enabled) ltOutline.enabled = false; return; }
            ltMarker.ForceMeshUpdate();
            TMP_TextInfo info = ltMarker.textInfo;
            int at = -1;
            for (int i = 0; info != null && i < info.characterCount; i++) if (info.characterInfo[i].elementType == TMP_TextElementType.Sprite) { at = i; break; }
            if (at < 0) { ltOutline.enabled = false; return; }
            TMP_CharacterInfo ci = info.characterInfo[at];
            TMP_SpriteAsset sa = ci.textElement != null ? ci.textElement.textAsset as TMP_SpriteAsset : null;
            if (sa == null || sa.spriteSheet == null || ci.textElement.glyph == null) { ltOutline.enabled = false; return; }
            UnityEngine.TextCore.GlyphRect gr = ci.textElement.glyph.glyphRect;
            Vector3 bl = ci.vertex_BL.position, tr = ci.vertex_TR.position;
            float unitsPerPx = gr.width > 0 ? (tr.x - bl.x) / gr.width : 1f;
            string key = sa.name + "|" + gr.x + "," + gr.y + "," + gr.width + "," + gr.height + "|" + unitsPerPx.ToString("0.000");
            if (key != outlineFor)
            {
                outlineFor = key;
                int pad = Mathf.Max(1, Mathf.CeilToInt(LtRim / Mathf.Max(0.0001f, unitsPerPx)));
                Sprite sp = OutlineSprite(sa.spriteSheet, gr, pad);
                ltOutline.sprite = sp;
                outlineCentre = (Vector2)((bl + tr) * 0.5f);
                outlineSize = new Vector2((gr.width + 2 * pad) * unitsPerPx, (gr.height + 2 * pad) * unitsPerPx);
            }
            if (ltOutline.sprite == null) { ltOutline.enabled = false; return; }
            float ms = ltMarker.transform.localScale.x;
            ltOutline.rectTransform.anchoredPosition = LtAt(row, compact) + outlineCentre * ms;
            ltOutline.rectTransform.sizeDelta = outlineSize * ms;
            ltOutline.color = new Color(0f, 0f, 0f, LtRimAlpha * k);
            if (!ltOutline.enabled) ltOutline.enabled = true;
        }
        // the glyph's pixels, grown by pad all round: each pixel takes the strongest coverage within pad of it
        static Sprite OutlineSprite(Texture sheet, UnityEngine.TextCore.GlyphRect gr, int pad)
        {
            try
            {
                RenderTexture rt = RenderTexture.GetTemporary(sheet.width, sheet.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(sheet, rt);
                RenderTexture was = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D src = new Texture2D(gr.width, gr.height, TextureFormat.RGBA32, false);
                src.ReadPixels(new Rect(gr.x, gr.y, gr.width, gr.height), 0, 0);
                src.Apply();
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
                Color32[] s = src.GetPixels32();
                UnityEngine.Object.Destroy(src);
                int w = gr.width + 2 * pad, h = gr.height + 2 * pad;
                Color32[] o = new Color32[w * h];
                int r2 = pad * pad;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        byte best = 0;
                        for (int dy = -pad; dy <= pad && best < 255; dy++)
                        {
                            int sy = y - pad + dy;
                            if (sy < 0 || sy >= gr.height) continue;
                            for (int dx = -pad; dx <= pad; dx++)
                            {
                                if (dx * dx + dy * dy > r2) continue;
                                int sx = x - pad + dx;
                                if (sx < 0 || sx >= gr.width) continue;
                                byte aa = s[sy * gr.width + sx].a;
                                if (aa > best) { best = aa; if (best == 255) break; }
                            }
                        }
                        o[y * w + x] = new Color32(255, 255, 255, best);
                    }
                Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Bilinear; tex.wrapMode = TextureWrapMode.Clamp;
                tex.SetPixels32(o); tex.Apply();
                return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception e) { Plugin.Log.LogWarning("LT outline: " + e.Message); return null; }
        }

        static int laidRow = -1, laidTwo = -1;
        static void Layout(bool row, bool two)
        {
            bool compact = !row && Plugin.CompactHotbar;
            int mode = row ? 1 : compact ? 2 : 0;
            if (laidRow == mode && laidTwo == (two ? 1 : 0)) return;
            laidRow = mode; laidTwo = two ? 1 : 0;
            float edge = Offset[0].y - SquareSize * 0.5f, below = -(SquareSize * 0.5f + RowGlyphGap);
            for (int i = 0; i < 4; i++)
            {
                Vector2 at = row ? new Vector2(RowX[i] * RowPitch, 0f) : compact ? CompactAt(i) : Offset[i];
                if (squares[i] != null) { ((RectTransform)squares[i].border.transform.parent).anchoredPosition = at; squares[i].home = at; }
                if (squares2[i] != null) { ((RectTransform)squares2[i].border.transform.parent).anchoredPosition = at; squares2[i].home = at; }
                if (hudPadGlyphs != null) { hudPadGlyphs[i].rectTransform.anchoredPosition = compact ? CompactCorner(i) : at + new Vector2(0f, below); hudPadGlyphs[i].fontSize = 15f; }   /* the compact cross as the row (18 until 0.89.16, Rob) */
                // the face glyphs: under their slots in the row, on their outer edges in the compact cross, on the squares'
                // inner edges in the cross
                Vector2 faceAt = row ? at + new Vector2(0f, below) : compact ? CompactCorner(i) : i == Up ? new Vector2(0f, DiamondAt) : i == Down ? new Vector2(0f, -DiamondAt) : i == Left ? new Vector2(-DiamondAt, 0f) : new Vector2(DiamondAt, 0f);
                if (hudFace != null) { hudFace[i].rectTransform.anchoredPosition = faceAt; hudFace[i].fontSize = row || compact ? 15f : DiamondSize; }
            }
            ((RectTransform)hudPrimary.transform).anchoredPosition = row ? new Vector2(two ? -RowHalf : 0f, 0f) : new Vector2(two ? -SetGap : 0f, 0f);   // laid out again when the style changes
            ((RectTransform)hudSecondary.transform).anchoredPosition = row ? new Vector2(RowHalf, 0f) : Vector2.zero;
            // in the row: smaller, centred on the top-left corner of the LT group's first slot (X)
            ltMarker.rectTransform.anchoredPosition = LtAt(row, compact);
            ltMarker.fontSize = 20f;   // as large as the cross's, in every style (the row's was 15 until 0.82.3)
            if (ltFrame != null) { ltFrame.rectTransform.anchoredPosition = LtAt(row, compact); ltTint.rectTransform.anchoredPosition = LtAt(row, compact); ltGlow.rectTransform.anchoredPosition = LtAt(row, compact); }
            outlineFor = null;   // measured again at the new size
        }

        static void BuildHud()
        {
            Hud hud = Hud.instance;
            if (hud == null || hud.m_rootObject == null) return;
            Vector2 mid = new Vector2(0.5f, 0.5f), bottomRight = new Vector2(1f, 0f);
            TMP_Text like = MainFont(hud.m_rootObject.transform);
            laidRow = laidTwo = -1;   // fresh objects: lay them out again
            hudRoot = Hints.New("ControllerPro_Favorites", hud.m_rootObject.transform);
            Hints.Place(hudRoot, bottomRight, bottomRight, mid, Vector2.zero, new Vector2(170f, 170f));

            // Left to right: the D-pad set, then the face-button set. The face set takes the anchor spot; the D-pad set
            // sits to its left. (With no face set, the D-pad set takes the anchor spot itself.)
            hudPrimary = Hints.New("primary", hudRoot.transform);
            Hints.Place(hudPrimary, mid, mid, mid, new Vector2(-SetGap, 0f), new Vector2(170f, 170f));
            dpadIcon = Hints.Img(Hints.New("dpad", hudPrimary.transform), Hints.Dpad(), DpadIdle);
            Hints.Place(dpadIcon.gameObject, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
            // The game's own D-pad glyph goes in the middle, the same one it uses in its prompts, and it follows the
            // controller type like every other glyph. The mod's drawn cross stays only as a fallback.
            dpadGlyph = Hints.Text(Hints.New("dpadGlyph", hudPrimary.transform), like, 24f, Color.white, TextAlignmentOptions.Center);
            Hints.Place(dpadGlyph.gameObject, mid, mid, mid, Vector2.zero, new Vector2(40f, 40f));
            BuildSquares(hudPrimary.transform, squares, like, false);
            hudPadGlyphs = new TMP_Text[4];
            for (int i = 0; i < 4; i++)
            {
                hudPadGlyphs[i] = Hints.Text(Hints.New("padGlyph" + i, hudPrimary.transform), like, 15f, Color.white, TextAlignmentOptions.Center);
                Hints.Place(hudPadGlyphs[i].gameObject, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
            }

            hudSecondary = Hints.New("secondary", hudRoot.transform);
            Hints.Place(hudSecondary, mid, mid, mid, Vector2.zero, new Vector2(170f, 170f));
            BuildSquares(hudSecondary.transform, squares2, like, false);
            hudFace = BuildFaceDiamond(hudSecondary.transform, like);
            // LT brings this set to life, so its glyph sits in the middle of the set. Held, it gives way to the four
            // button glyphs on the squares' inner edges.
            // LT's own square, lit while LT is held (the row and the compact cross): the slots' white layer and their LT frame
            // the row's LT, held: a soft white glow around it (0.82.6), under its square
            ltGlow = Hints.Img(Hints.New("ltGlow", hudSecondary.transform), Hints.Glow(), new Color(1f, 1f, 1f, 0f));
            Hints.Place(ltGlow.gameObject, mid, mid, mid, Vector2.zero, new Vector2(LtBox * 2f, LtBox * 2f));
            ltTint = Hints.Img(Hints.New("ltSquare", hudSecondary.transform), Hints.Plate((int)LtBox), new Color(1f, 1f, 1f, 0f));   // the slots' corner radius at its own size
            Hints.Place(ltTint.gameObject, mid, mid, mid, Vector2.zero, new Vector2(LtBox, LtBox));
            ltFrame = Hints.Img(Hints.New("ltSquareFrame", hudSecondary.transform), null, Color.clear);
            Canvas lcv = ltFrame.canvas;
            float lline = HaloLine * (lcv != null && lcv.referencePixelsPerUnit > 0f ? lcv.referencePixelsPerUnit / 100f : 1f);
            ltFrame.sprite = Hints.Frame(LtBox, HaloGap, lline); ltFrame.raycastTarget = false;
            Hints.Place(ltFrame.gameObject, mid, mid, mid, Vector2.zero, new Vector2(LtBox + 2f * (HaloGap + lline), LtBox + 2f * (HaloGap + lline)));
            // LT's dark backing in LT's own shape: the same glyph (the game's, for whichever controller), drawn larger behind it
            // and tinted black (0.82.7; a square before)
            // an even outline (0.82.9): copies of the glyph at LT's size, nudged LtRim out in a ring of directions, solid black,
            // so the rim is the same thickness all round (one enlarged copy made it thicker where the shape is wider)
            ltOutline = Hints.Img(Hints.New("ltOutline", hudSecondary.transform), null, new Color(0f, 0f, 0f, 0f));
            Hints.Place(ltOutline.gameObject, mid, mid, mid, Vector2.zero, new Vector2(40f, 40f));
            ltOutline.enabled = false;
            ltMarker = Hints.Text(Hints.New("lt", hudSecondary.transform), like, 20f, Color.white, TextAlignmentOptions.Center);
            Hints.Place(ltMarker.gameObject, mid, mid, mid, Vector2.zero, new Vector2(40f, 40f));
            hudSecondary.SetActive(false);
        }
        // The held line's edges put on whole screen pixels (0.89.7): a slot between pixels drew its fine line sharp on one
        // side and smeared over two pixels on the other, so one side looked thicker. The line keeps the slot's place
        // otherwise (stretched over it, nudged by under a pixel at each edge).
        static readonly Vector3[] snapC = new Vector3[4];
        static void SnapToPixels(RectTransform r)
        {
            RectTransform par = r.parent as RectTransform;
            Canvas cv = r.GetComponentInParent<Canvas>();
            if (par == null || cv == null) return;
            Camera cam = cv.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : cv.rootCanvas.worldCamera;
            par.GetWorldCorners(snapC);
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, snapC[0]), tr = RectTransformUtility.WorldToScreenPoint(cam, snapC[2]);
            float pxW = tr.x - bl.x, pxH = tr.y - bl.y;
            if (pxW < 1f || pxH < 1f) return;
            Vector2 per = new Vector2(par.rect.width / pxW, par.rect.height / pxH);   // the slot's own units per screen pixel
            Vector2 lo = new Vector2((Mathf.Round(bl.x) - bl.x) * per.x, (Mathf.Round(bl.y) - bl.y) * per.y);
            Vector2 hi = new Vector2((Mathf.Round(tr.x) - tr.x) * per.x, (Mathf.Round(tr.y) - tr.y) * per.y);
            if (r.offsetMin != lo) r.offsetMin = lo;
            if (r.offsetMax != hi) r.offsetMax = hi;
        }
        // Each slot put on whole screen pixels (0.90.1, Rob): the slots stand the same distance either side of their set's
        // middle, so their edges fell on opposite fractions of a pixel and the frame around X looked wider on one side, B's on
        // the other. Snapped, every slot draws the same way. Nudged by under a pixel from its own place.
        static void SnapSlot(Square q)
        {
            if (q == null || q.border == null) return;
            RectTransform sq = q.border.transform.parent as RectTransform;
            if (sq == null || !sq.gameObject.activeInHierarchy) return;
            sq.anchoredPosition = q.home;
            if (sq.sizeDelta.x != SquareSize || sq.sizeDelta.y != SquareSize) sq.sizeDelta = new Vector2(SquareSize, SquareSize);
            Canvas cv = sq.GetComponentInParent<Canvas>();
            if (cv == null) return;
            Camera cam = cv.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : cv.rootCanvas.worldCamera;
            sq.GetWorldCorners(snapC);
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, snapC[0]), tr = RectTransformUtility.WorldToScreenPoint(cam, snapC[2]);
            float pxW = tr.x - bl.x, pxH = tr.y - bl.y;
            if (pxW < 1f || pxH < 1f) return;
            /* and a whole number of pixels across (0.90.2): with only its corner on a pixel, its far edge still fell between two,
               and the frame and line came out uneven side to side. Kept centred, everything on it now sits evenly. */
            float wN = Mathf.Max(1f, Mathf.Round(pxW)), hN = Mathf.Max(1f, Mathf.Round(pxH));
            float ux = SquareSize / pxW, uy = SquareSize / pxH;   /* the slot's units per pixel */
            sq.sizeDelta = new Vector2(wN * ux, hN * uy);
            float cx = (bl.x + tr.x) * 0.5f, cy = (bl.y + tr.y) * 0.5f;
            float left = cx - wN * 0.5f, bottom = cy - hN * 0.5f;
            Vector2 d = new Vector2((Mathf.Round(left) - left) * ux, (Mathf.Round(bottom) - bottom) * uy);
            sq.anchoredPosition = q.home + d;
        }
        static void UpdateHud(Player p, bool secondary, bool ltHeld)
        {
            if (hudRoot == null) BuildHud();
            if (hudRoot == null) return;
            // hidden with the inventory open: the crafting panel takes that side of the screen, and the overlay shows the favorites itself
            if (Hud.IsUserHidden() || Plugin.BagCovers || Minimap.IsOpen()) { hudRoot.SetActive(false); anyQueued = false; ClearBars(false); return; }   // also out of the way of the large map
            hudRoot.SetActive(true);
            RectTransform rt = (RectTransform)hudRoot.transform;
            bool row = Plugin.RowHotbar;
            Layout(row, secondary);
            bool centred = Plugin.HotbarCentred;
            float sc = Mathf.Max(0.4f, Plugin.FavScale.Value);
            rt.localScale = new Vector3(sc, sc, 1f);
            // how far the hotbar reaches to the left and right of its own middle (the D-pad set sits to the left)
            float edgeSq = (Plugin.CompactHotbar && !row ? CompactX : Offset[3].x) + SquareSize * 0.5f, rowHalf = 1.5f * RowPitch + SquareSize * 0.5f;
            float reachL = (row ? (secondary ? RowHalf + rowHalf : rowHalf) : (secondary ? SetGap + edgeSq : edgeSq)) * sc;
            float reachR = (row ? (secondary ? RowHalf + rowHalf : rowHalf) : edgeSq) * sc;
            RectTransform space = rt.parent as RectTransform;
            // Every corner piece keeps the minimap's own distance from the screen edges: the hotbar's lowest edge (the cross's
            // bottom square, or the bottom of the row's slots) and its outer side sit that far from the bottom and the side.
            float edgeIn = MapEdge(space);
            // the row is measured by its slots alone: the glyphs under them hang into the edge space (Rob, 0.66.1)
            bool compact = !row && Plugin.CompactHotbar, edgy = row || compact;   // edgy: each slot shows its own button
            float reachDown = (row ? SquareSize * 0.5f : compact ? CompactY + SquareSize * 0.5f : -Offset[1].y + SquareSize * 0.5f) * sc;
            float y = MapEdge(space, true) + reachDown;   // its bottom on the map's bottom line
            if (centred)
            {
                // centred along the bottom of the screen, low enough to stay clear of the character
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(row || !secondary ? 0f : SetGap * 0.5f * sc, y);
            }
            else if (Plugin.HotbarLeft)
            {
                // the bottom left corner, as far in from the left edge as the right-hand hotbar is from the right edge.
                // If the resource bars already take that corner (the game's own, or the Horizontal ones at the bottom left), the
                // hotbar sits just to their right instead of on top of them.
                float x = edgeIn + reachL;
                float taken = LeftTaken(space);
                if (taken > 0f) x = Mathf.Max(x, taken + CornerGap + reachL);
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
                rt.anchoredPosition = new Vector2(x, y);
            }
            else
            {
                // the bottom right corner. With the minimap moved to the bottom right, the hotbar sits just to its left.
                float x = edgeIn + reachR;
                float taken = RightTaken(space);
                if (taken > 0f) x = Mathf.Max(x, taken + CornerGap + reachR);
                rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
                rt.anchoredPosition = new Vector2(-x, y);
            }            // Sprinting: the whole cluster takes a disabled look, and eases back when the sprint ends.
            // The cluster no longer dims when its items cannot be used (sprinting, dodging, mid-swing, guarding, the build
            // menu). In play that was constant flicker. It always shows its normal look; the one sign that an item cannot
            // be used right now is the red flash on the square when it is pressed.
            /* the one disabled look, while a radial wheel is open (0.88.3): the wheel has the buttons, so the hotbar steps back */
            bool sprinting = Hud.InRadial();
            hudAlpha = Mathf.MoveTowards(hudAlpha, sprinting ? SprintAlpha : 1f, Time.unscaledDeltaTime / 0.12f);
            anyQueued = AnyQueued(p);
            foreach (Square sq in squares) SnapSlot(sq);
            if (secondary) foreach (Square sq in squares2) SnapSlot(sq);
            FillSquares(squares, p, 0, null, 1f, hudAlpha);
            string padGlyph = Hints.DpadGlyph();
            bool gameGlyph = !string.IsNullOrEmpty(padGlyph);
            if (dpadGlyph.text != padGlyph) dpadGlyph.text = padGlyph;
            dpadGlyph.alpha = edgy ? 0f : hudAlpha;   // the row and the compact cross name each slot's own direction instead
            if (dpadIcon.enabled == (gameGlyph || edgy)) dpadIcon.enabled = !gameGlyph && !edgy;
            for (int i = 0; i < 4; i++)
            {
                if (hudPadGlyphs[i].gameObject.activeSelf != edgy) hudPadGlyphs[i].gameObject.SetActive(edgy);
                if (edgy) { string g = Hints.Glyph(PadGlyphName(i)); if (hudPadGlyphs[i].text != g) hudPadGlyphs[i].text = g; hudPadGlyphs[i].alpha = hudAlpha; }
            }
            Color dc = DpadIdle; dc.a = Mathf.Min(dc.a, hudAlpha); dpadIcon.color = dc;

            // the LT marker says "hold this and something else appears"
            if (secondary)
            {
                string g = Hints.Glyph("JoyLTrigger");
                if (ltMarker.text != g) ltMarker.text = g;
                bool lb = LtBetween(row);   /* LT between the sets (the row, and both crosses since 0.90.0): always shown, pressed look, glow */
                // LT in the middle at rest; held, it fades out as the button glyphs fade in on the squares' edges
                ltK = Mathf.MoveTowards(ltK, ltHeld ? 1f : 0f, Time.unscaledDeltaTime / 0.08f);
                // in the row LT stays in the gap between the two groups, always shown, since the glyphs under the LT
                // slots are always shown too
                ltMarker.alpha = Mathf.Min(lb ? 1f : 1f - ltK, hudAlpha);   // both crosses: LT fades out while held
                // in the row, LT shows being pressed while held: a touch smaller and a pixel lower
                // held: LT's square lights as the slots do (their white layer and their LT frame); no more dip
                ltMarker.transform.localScale = Vector3.one * (lb ? 1f - 0.07f * ltK : 1f);   // the row: a touch smaller while held, to show it is down (93%; 88% until 0.82.10)
                ltMarker.rectTransform.anchoredPosition = LtAt(row, compact);
                // no frame, only the fill (Rob, 0.81.1)
                if (ltFrame != null) { ltFrame.color = Color.clear; ltTint.color = Color.clear; ltGlow.color = new Color(1f, 1f, 1f, LtGlow * (lb ? ltK * hudAlpha : 0f)); }
                LtOutline(row, compact);
            }
            // The secondary set is always on screen at full strength. Holding LT is shown by the button glyphs coming
            // up and the squares taking a brighter edge, not by the set brightening.
            if (hudSecondary.activeSelf != secondary) hudSecondary.SetActive(secondary);
            if (secondary)
            {
                // the LT group is at full strength at rest, in the row as in the cross (the row's sat back at 45% until 0.66.1)
                float groupAlpha = 1f;
                FillSquares(squares2, p, 4, null, groupAlpha, hudAlpha);
                // Held (Rob's pen.dev frame, 0.82.0): no outline; a dark plate (black 50%, 4 larger all round, its corners
                // concentric) comes up behind each square, and the square's black gives way to white at 20%. Until 0.82.0 the
                // square kept its black under an 8% white layer, with a white outline 2 clear of it.
                // (0.82.1: the square keeps its black; only the plate behind it comes up)
                Color restBox = BoxRest;
                Color white = Color.clear;
                /* the D-pad set takes the same held look (0.87.4): with LT held it does something else too (the actions, or
                   swapped, the items) */
                foreach (Square[] heldSet in new Square[][] { squares2, squares })
                foreach (Square q in heldSet)
                {
                    if (q == null) continue;
                    /* 1.0.1: with the LT + D-pad layer off, LT does nothing to the D-pad, so its squares keep their resting look */
                    float k = heldSet == squares && !Plugin.LtDpadLayer.Value ? 0f : ltK;
                    Color back = new Color(0f, 0f, 0f, LtBackAlpha * k * hudAlpha);
                    if (q.halo != null && q.halo.color != Color.clear) q.halo.color = Color.clear;
                    if (q.bg != null && q.bg.color != restBox) q.bg.color = restBox;
                    if (q.tint != null && q.tint.color != white) q.tint.color = white;
                    if (q.back != null && q.back.color != back) q.back.color = back;
                    Color line = new Color(1f, 1f, 1f, HeldLineAlpha * k * hudAlpha), glow = new Color(1f, 1f, 1f, HeldGlowAlpha * k * hudAlpha);
                    if (q.heldLine != null && q.heldLine.color != line) q.heldLine.color = line;
                    if (q.heldLine != null && line.a > 0.001f) SnapToPixels(q.heldLine.rectTransform);
                    if (q.heldGlow != null && q.heldGlow.color != glow) q.heldGlow.color = glow;
                }
                FaceDiamond(hudFace);
                // row and compact cross: the face buttons' glyphs sit back until LT is held (0.81.3), the cross's come in with LT
                /* resting: darkened, not faded (0.91.16, Rob: at 30% they read as see-through); the archived middle look still fades */
                if (hudFace != null) foreach (TMP_Text g in hudFace)
                {
                    if (LtBetween(row))
                    {
                        float v = Mathf.Lerp(FaceDark, 1f, ltK);
                        Color want = new Color(v, v, v, hudAlpha);
                        if (g.color != want) g.color = want;
                    }
                    else g.alpha = Mathf.Min(ltK, hudAlpha);
                }   // the row's sit back at 45%; both crosses' only show while LT is held (0.81.6)
            }
            ClearBars(centred);   // only at the bottom centre does the hotbar meet the game's stamina bars
        }

        // ----- vanilla hotbar: hidden while favorites are in use, since the D-pad no longer drives it -----
        static CanvasGroup[] hotbars;
        static void SetHotbarHidden(bool hidden)
        {
            if (hotbars == null)
            {
                if (Hud.instance == null) return;
                HotkeyBar[] bars = Hud.instance.GetComponentsInChildren<HotkeyBar>(true);
                hotbars = new CanvasGroup[bars.Length];
                for (int i = 0; i < bars.Length; i++)
                {
                    hotbars[i] = bars[i].GetComponent<CanvasGroup>();
                    if (hotbars[i] == null) hotbars[i] = bars[i].gameObject.AddComponent<CanvasGroup>();
                }
            }
            foreach (CanvasGroup g in hotbars)
            {
                if (g == null) { hotbars = null; return; }   // the HUD was rebuilt: look again next frame
                g.alpha = hidden ? 0f : 1f;
            }
        }

        // ----- assign overlay -----
        // Behaves like the game's split dialog: a large window over the inventory with the inventory dimmed behind it.
        // Its dim layers, window art, title and the two square buttons are copies of the split dialog's own, so every
        // overlay in the mod looks and reads the same as the game's.
        static GameObject pickRoot;
        static RectTransform pickPanel, splitAnchor;
        static Image pickItemIcon;
        static TMP_Text pickItemName, pickLabel2, pickLabel1;
        static GameObject pickCross2;
        static Image pickCarryIcon, pickCarryIcon2;
        static GameObject focusFx;
        static bool focusLooked;
        static GameObject pickCarry;
        static GameObject pickHome, pickCross1;
        static TMP_Text[] pickPadGlyphs;
        static int pickLaid = -1;
        static Image holdIcon, holdIcon2, holdFrame;
        const float TrashX = 0.6f;   // the red X held back a little, so what is set down in the slot reads over it
        static readonly Square[] pickSquares = new Square[4], pickSquares2 = new Square[4];
        static readonly FieldInfo F_sdPanel = AccessTools.Field(typeof(SplitDialog), "m_panel");
        static readonly FieldInfo F_sdNormalPos = AccessTools.Field(typeof(SplitDialog), "m_panelNormalPosition");

        // Copies one of the game's art layers under a new parent, keeping the same margin around it even if the parent is a different size.
        static void CloneLayer(Transform src, Transform parent, RectTransform srcParent)
        {
            GameObject go = UnityEngine.Object.Instantiate(src.gameObject, parent, false);
            go.name = "cp_" + src.name;
            RectTransform rt = (RectTransform)go.transform;
            Vector2 mid = new Vector2(0.5f, 0.5f);
            Vector2 size = ((RectTransform)src).rect.size, parentSize = srcParent.rect.size;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = mid; rt.anchoredPosition = Vector2.zero; rt.sizeDelta = size - parentSize;
            foreach (Graphic g in go.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            go.SetActive(true);
        }

        public static void HidePicker() { if (pickRoot != null && pickRoot.activeSelf) pickRoot.SetActive(false); }
        // the window while it is open, for the button guide to sit in
        public static RectTransform PickPanel { get { return pickRoot != null && pickRoot.activeInHierarchy ? pickPanel : null; } }

        // The mod's window, as the Controller Hotbar window is made: a copy of the split dialog's dim layer over the inventory,
        // its wood fill and frames as the window, and its carved title. Returns the root (switched off); panel is the window.
        public static GameObject Shell(InventoryGui gui, string name, Vector2 size, string titleText, out RectTransform panel, out TMP_Text title)
        {
            Vector2 mid = new Vector2(0.5f, 0.5f), top = new Vector2(0.5f, 1f);
            TMP_Text like = gui.m_weight != null ? gui.m_weight : gui.m_containerName;
            SplitDialog sd = gui.m_splitDialog;
            RectTransform sdRoot = sd != null ? sd.transform as RectTransform : null;
            RectTransform sdPanel = sd != null && F_sdPanel != null ? F_sdPanel.GetValue(sd) as RectTransform : null;
            GameObject shell = Hints.New(name, sdRoot != null ? sdRoot.parent : gui.transform);
            RectTransform root = Hints.Place(shell, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
            if (sdRoot != null) { root.anchorMin = sdRoot.anchorMin; root.anchorMax = sdRoot.anchorMax; root.pivot = sdRoot.pivot; root.anchoredPosition = sdRoot.anchoredPosition; root.sizeDelta = sdRoot.sizeDelta; }
            int dim = 0, art = 0;
            if (sdRoot != null)
                foreach (Transform ch in sdRoot)
                    if (ch != sdPanel && ch.name.ToLowerInvariant().Contains("darken") && ch.GetComponent<Image>() != null) { CloneLayer(ch, shell.transform, sdRoot); dim++; }
            if (dim == 0) Hints.Img(shell, null, new Color(0f, 0f, 0f, 0.314f));
            GameObject pg = Hints.New("panel", shell.transform);
            panel = Hints.Place(pg, mid, mid, mid, Vector2.zero, size);
            if (sdPanel != null)
                foreach (Transform ch in sdPanel)
                {
                    string n = ch.name.ToLowerInvariant();
                    if ((n.StartsWith("bkg") || n.StartsWith("border")) && ch.GetComponent<Image>() != null) { CloneLayer(ch, pg.transform, sdPanel); art++; }
                }
            if (art == 0) Hints.Img(pg, null, new Color(0f, 0f, 0f, 0.9f));
            Transform sdTitle = sdPanel != null ? sdPanel.Find("Text") : null;
            if (sdTitle != null && sdTitle.GetComponent<TMP_Text>() != null)
            {
                GameObject tg = UnityEngine.Object.Instantiate(sdTitle.gameObject, pg.transform, false);
                tg.name = "cp_title"; tg.SetActive(true);
                title = tg.GetComponent<TMP_Text>();
                RectTransform tr = (RectTransform)tg.transform;
                tr.anchorMin = top; tr.anchorMax = top; tr.pivot = top; tr.anchoredPosition = new Vector2(0f, -6f);
            }
            else
            {
                title = Hints.Text(Hints.New("title", pg.transform), like, 22f, Color.white, TextAlignmentOptions.Center);
                Hints.Place(title.gameObject, new Vector2(0f, 1f), new Vector2(1f, 1f), top, new Vector2(0f, -14f), new Vector2(0f, 30f));
            }
            title.text = titleText;
            Plugin.Log.LogInfo(name + ": copied " + dim + " dim layers, " + art + " window art layers, title " + (sdTitle != null));
            shell.SetActive(false);
            return shell;
        }

        public static void ShowPicker(InventoryGui gui, ItemDrop.ItemData item)
        {
            Player p = Player.m_localPlayer;
            if (p == null) return;   // item is null when opened with LT + Start: nothing in hand
            EnsureLoaded(p);
            Vector2 mid = new Vector2(0.5f, 0.5f), top = new Vector2(0.5f, 1f);
            bool two = SecondaryAvailable();
            if (pickRoot == null)
            {
                TMP_Text like = gui.m_weight != null ? gui.m_weight : gui.m_containerName;
                SplitDialog sd = gui.m_splitDialog;
                RectTransform sdRoot = sd != null ? sd.transform as RectTransform : null;
                RectTransform sdPanel = sd != null && F_sdPanel != null ? F_sdPanel.GetValue(sd) as RectTransform : null;
                splitAnchor = sd != null && F_sdNormalPos != null ? F_sdNormalPos.GetValue(sd) as RectTransform : null;
                if (splitAnchor == null) splitAnchor = sdPanel;

                // dim: a copy of the split dialog's own darken layer, which is black at 31% opacity. Its blur layer is left
                // out: copying it gave an effect the split dialog itself does not show.
                pickRoot = Hints.New("ControllerPro_AssignFavorite", sdRoot != null ? sdRoot.parent : gui.transform);
                RectTransform root = Hints.Place(pickRoot, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                if (sdRoot != null) { root.anchorMin = sdRoot.anchorMin; root.anchorMax = sdRoot.anchorMax; root.pivot = sdRoot.pivot; root.anchoredPosition = sdRoot.anchoredPosition; root.sizeDelta = sdRoot.sizeDelta; }
                int dimLayers = 0, artLayers = 0;
                if (sdRoot != null)
                    foreach (Transform ch in sdRoot)
                        if (ch != sdPanel && ch.name.ToLowerInvariant().Contains("darken") && ch.GetComponent<Image>() != null) { CloneLayer(ch, pickRoot.transform, sdRoot); dimLayers++; }
                if (dimLayers == 0) Hints.Img(pickRoot, null, new Color(0f, 0f, 0f, 0.314f));

                // window: copies of the split window's wood fill and both of its frames, stretched to this window's size
                GameObject panel = Hints.New("panel", pickRoot.transform);
                pickPanel = Hints.Place(panel, mid, mid, mid, Vector2.zero, new Vector2(two ? 620f : 400f, 390f));   // only the title, the two sets and their labels are in it (the button guide sits under it)
                if (sdPanel != null)
                    foreach (Transform ch in sdPanel)
                    {
                        string n = ch.name.ToLowerInvariant();
                        if ((n.StartsWith("bkg") || n.StartsWith("border")) && ch.GetComponent<Image>() != null) { CloneLayer(ch, panel.transform, sdPanel); artLayers++; }
                    }
                if (artLayers == 0)
                {
                    Image bg = Hints.Img(panel, null, new Color(0f, 0f, 0f, 0.9f));
                    if (Plugin.GamePanelStyle.Value) Hints.Style(bg, gui.m_player);
                }

                // title: a copy of the split dialog's own carved title
                Transform sdTitle = sdPanel != null ? sdPanel.Find("Text") : null;
                TMP_Text title;
                if (sdTitle != null && sdTitle.GetComponent<TMP_Text>() != null)
                {
                    GameObject tg = UnityEngine.Object.Instantiate(sdTitle.gameObject, panel.transform, false);
                    tg.name = "cp_title"; tg.SetActive(true);
                    title = tg.GetComponent<TMP_Text>();
                    RectTransform tr = (RectTransform)tg.transform;
                    tr.anchorMin = top; tr.anchorMax = top; tr.pivot = top; tr.anchoredPosition = new Vector2(0f, -6f);
                }
                else
                {
                    title = Hints.Text(Hints.New("title", panel.transform), like, 22f, Color.white, TextAlignmentOptions.Center);
                    Hints.Place(title.gameObject, new Vector2(0f, 1f), new Vector2(1f, 1f), top, new Vector2(0f, -14f), new Vector2(0f, 30f));
                }
                title.text = "Controller Hotbar";

                pickItemIcon = Hints.Img(Hints.New("item", panel.transform), null, Color.white);
                pickItemIcon.preserveAspect = true;
                Hints.Place(pickItemIcon.gameObject, top, top, top, new Vector2(0f, -56f), new Vector2(44f, 44f));
                pickItemName = Hints.Text(Hints.New("itemName", panel.transform), like, 16f, new Color(1f, 1f, 1f, 0.85f), TextAlignmentOptions.Center);
                Hints.Place(pickItemName.gameObject, new Vector2(0f, 1f), new Vector2(1f, 1f), top, new Vector2(0f, -104f), new Vector2(0f, 22f));

                // the two sets, in the same arrangement as on the HUD: face buttons on the left, D-pad on the right
                float side = two ? 150f : 0f;
                GameObject cross1 = Hints.New("dpadSet", panel.transform);
                Hints.Place(cross1, mid, mid, mid, new Vector2(-side, -15f), new Vector2(170f, 170f));   // D-pad set on the left
                cross1.transform.localScale = new Vector3(1.3f, 1.3f, 1f);
                Image dp = Hints.Img(Hints.New("dpad", cross1.transform), Hints.Dpad(), Color.white);
                pickDpadIcon = dp;
                pickDpadGlyph = Hints.Text(Hints.New("dpadGlyph", cross1.transform), like, 24f, Color.white, TextAlignmentOptions.Center);
                Hints.Place(pickDpadGlyph.gameObject, mid, mid, mid, Vector2.zero, new Vector2(40f, 40f));
                Hints.Place(dp.gameObject, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
                BuildSquares(cross1.transform, pickSquares, like, false);
                pickCross1 = cross1;
                // the D-pad set's own glyph under each square, shown in the row layout (as on the row hotbar)
                pickPadGlyphs = new TMP_Text[4];
                for (int g = 0; g < 4; g++)
                {
                    pickPadGlyphs[g] = Hints.Text(Hints.New("padGlyph" + g, cross1.transform), like, 15f, Color.white, TextAlignmentOptions.Center);
                    Hints.Place(pickPadGlyphs[g].gameObject, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
                }
                pickLabel1 = Hints.Text(Hints.New("dpadLabel", panel.transform), like, 20f, new Color(1f, 1f, 1f, 0.7f), TextAlignmentOptions.Center);   // was 14
                Hints.Place(pickLabel1.gameObject, mid, mid, mid, new Vector2(-side, -164f), new Vector2(300f, 28f));   // 24 clear of the bottom square (was 10)

                pickCross2 = Hints.New("faceSet", panel.transform);
                Hints.Place(pickCross2, mid, mid, mid, new Vector2(side, -15f), new Vector2(170f, 170f));   // face-button set on the right
                pickCross2.transform.localScale = new Vector3(1.3f, 1.3f, 1f);
                BuildSquares(pickCross2.transform, pickSquares2, like, false);
                pickFace = BuildFaceDiamond(pickCross2.transform, like);
                pickLabel2 = Hints.Text(Hints.New("faceLabel", panel.transform), like, 20f, new Color(1f, 1f, 1f, 0.7f), TextAlignmentOptions.Center);   // was 14
                Hints.Place(pickLabel2.gameObject, mid, mid, mid, new Vector2(side, -164f), new Vector2(300f, 28f));

                // No buttons in the window. Changes count as they are made, and B closes it, as B closes everything else.
                Plugin.Log.LogInfo("Assign overlay: copied " + dimLayers + " dim layers, " + artLayers + " window art layers, title " + (sdTitle != null));
            }
            pickRoot.SetActive(true);
            pickRoot.transform.SetAsLastSibling();
            // in the middle of the screen (it used to open where the split window does, over the bag)
            pickPanel.position = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, pickPanel.position.z);

            // No item in the header: this is one place for arranging every favorite. It only starts with one item in hand.
            string name = pickMarked;
            if (pickItemIcon.gameObject.activeSelf) pickItemIcon.gameObject.SetActive(false);
            if (pickItemName.gameObject.activeSelf) pickItemName.gameObject.SetActive(false);
            pickLabel1.text = Hints.DpadGlyph();
            if (pickDpadGlyph != null)
            {
                string pg = Hints.DpadGlyph();
                if (pickDpadGlyph.text != pg) pickDpadGlyph.text = pg;
                if (pickDpadIcon != null) pickDpadIcon.enabled = string.IsNullOrEmpty(pg);   // the drawn cross only when the game has no glyph to give
            }
            pickCross2.SetActive(two);
            pickLabel2.gameObject.SetActive(two);
            if (two) pickLabel2.text = Hints.Glyph("JoyLTrigger") + "  +  " + Hints.Glyph("JoyButtonY") + Hints.Glyph("JoyButtonX") + Hints.Glyph("JoyButtonB") + Hints.Glyph("JoyButtonA");
            FaceDiamond(pickFace);
            PickLayout(Plugin.RowHotbar, two);
            FillSquares(pickSquares, p, 0, name, 1f, 1f);
            if (two) FillSquares(pickSquares2, p, 4, name, 1f, 1f);
            // the focus: a white frame on whatever it is on, a square or the holding slot
            if (pickFocus >= 4 && pickFocus < Home && !two) pickFocus = Up;
            bool noFocus = pickCursor && !pickOver;   // the cursor is up and over nothing
            bool onHold = !noFocus && pickFocus == Home;
            Square at = noFocus || onHold ? null : pickFocus < 4 ? pickSquares[pickFocus] : pickSquares2[pickFocus - 4];
            // In this window a square at rest has no outline. The outline is the focus, and only the focus.
            for (int i = 0; i < 4; i++)
            {
                if (pickSquares[i] != null) pickSquares[i].border.color = Color.clear;
                if (two && pickSquares2[i] != null) pickSquares2[i].border.color = Color.clear;
            }
            // The focus looks exactly as it does in the bag: the game's own selection frame, with its glow and its
            // light fill, copied from a bag slot and laid over the square (or the holding slot) the focus is on.
            if (!focusLooked)
            {
                focusLooked = true;
                try
                {
                    GameObject prefab = gui.m_playerGrid != null ? gui.m_playerGrid.m_elementPrefab : null;
                    InventoryElement el = prefab != null ? prefab.GetComponent<InventoryElement>() : null;
                    RectTransform slot = prefab != null ? prefab.transform as RectTransform : null;
                    if (el != null && el.m_selected != null && slot != null)
                    {
                        RectTransform src = el.m_selected.transform as RectTransform;
                        focusFx = UnityEngine.Object.Instantiate(el.m_selected, pickPanel.transform, false);
                        focusFx.name = "cp_focus";
                        foreach (Graphic g in focusFx.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                        // the frame keeps the same overhang around our square that it has around a bag slot
                        float square = SquareSize * 1.3f;
                        float w = src != null && slot.rect.width > 1f ? src.rect.width / slot.rect.width : 1.09f;
                        float h = src != null && slot.rect.height > 1f ? src.rect.height / slot.rect.height : 1.09f;
                        RectTransform frt = (RectTransform)focusFx.transform;
                        frt.anchorMin = mid; frt.anchorMax = mid; frt.pivot = mid; frt.sizeDelta = new Vector2(square * w, square * h);
                        Plugin.Log.LogInfo("Favorites window: using the bag's own selection frame for the focus (" + (src != null ? src.rect.width + " x " + src.rect.height : "size unknown") + " on a " + slot.rect.width + " slot)");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning("Favorites focus frame: " + e.Message); focusFx = null; }
            }
            // (the holding slot is built a few lines further down, the first time the window opens: until it exists
            // the frame cannot sit on it. This was the crash that shut the window the instant it opened on a new item.)
            bool focused = at != null || (onHold && pickHome != null);
            if (focusFx != null)
            {
                if (focusFx.activeSelf != focused) focusFx.SetActive(focused);
                if (focused)
                {
                    focusFx.transform.position = at != null ? at.border.transform.position : pickHome.transform.position;
                    focusFx.transform.SetAsLastSibling();   // over the squares; what is in hand and the cursor go on top of it below
                }
            }
            else if (at != null) at.border.color = Color.white;   // the game's frame could not be found: a plain white outline
            if (pickCarryIcon == null)
            {
                // The holding slot: between the two sets and a little above them. A faint X sits behind it to say that
                // what is left here is not kept.
                float size = SquareSize * 1.3f;
                pickHome = Hints.New("holding", pickPanel.transform);
                pickLaid = -1;   // laid out again now that the holding slot exists
                Hints.Place(pickHome, mid, mid, mid, new Vector2(0f, 78f), new Vector2(size, size));
                Hints.Img(pickHome, Hints.Plate(), new Color(0f, 0f, 0f, 0.6f));
                // the X: the game's own red cross, the one it lays over a buff's icon while it cools down (a potion's
                // cooldown), from its status effect art; two faint drawn bars if it cannot be found (until 0.75.0 always)
                Hud hud = Hud.instance;
                Transform cdT = hud != null && hud.m_statusEffectTemplate != null ? hud.m_statusEffectTemplate.Find("Cooldown") : null;
                Image cdArt = cdT != null ? cdT.GetComponent<Image>() : null;
                if (cdArt != null && cdArt.sprite != null)
                {
                    Color xc = cdArt.color; xc.a *= TrashX;
                    Image x = Hints.Img(Hints.New("x", pickHome.transform), cdArt.sprite, xc);
                    x.preserveAspect = true;
                    Hints.Place(x.gameObject, mid, mid, mid, Vector2.zero, new Vector2(44f, 44f));
                    Plugin.Log.LogInfo("Assign overlay: the holding slot's X is the game's '" + cdArt.sprite.name + "' (" + cdArt.color + ")");
                }
                else
                    for (int k = 0; k < 2; k++)
                    {
                        Image bar = Hints.Img(Hints.New("x" + k, pickHome.transform), Hints.Solid(), new Color(1f, 1f, 1f, 0.22f));
                        Hints.Place(bar.gameObject, mid, mid, mid, Vector2.zero, new Vector2(34f, 4f));
                        bar.transform.localRotation = Quaternion.Euler(0f, 0f, k == 0 ? 45f : -45f);
                    }
                holdIcon = Hints.Img(Hints.New("icon", pickHome.transform), null, Color.white);
                holdIcon.preserveAspect = true;
                Hints.Place(holdIcon.gameObject, mid, mid, mid, Vector2.zero, new Vector2(60f, 60f));
                holdIcon2 = Hints.Img(Hints.New("icon2", pickHome.transform), null, Color.white);
                holdIcon2.preserveAspect = true;
                Hints.Place(holdIcon2.gameObject, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-1f, 1f), new Vector2(39f, 39f));
                holdFrame = Hints.Img(Hints.New("border", pickHome.transform), Hints.Box(), BorderIdle);
                holdFrame.type = Image.Type.Sliced;
                Hints.Place(holdFrame.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);

                // What is in hand carries its own blue box with it: the same blue a square shows for the active item.
                // So the item being moved is the one thing in the window that is plainly "the one", from the moment
                // the window opens, and the place it came from is left showing only its ghost.
                pickCarry = Hints.New("inHand", pickPanel.transform);
                Hints.Place(pickCarry, mid, mid, mid, Vector2.zero, new Vector2(size, size));
                // A solid backing first. The blue art is see-through, and without this the frame of the square underneath
                // showed through the box being held over it.
                Hints.Img(pickCarry, Hints.Plate(), new Color(0.07f, 0.07f, 0.07f, 1f));
                Image blue = Hints.Img(Hints.New("blue", pickCarry.transform), Hints.Plate(), new Color(0.35f, 0.6f, 1f, 0.8f));
                Hints.Place(blue.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                Square like0 = pickSquares[0];
                if (like0 != null && like0.eqSprite != null) { blue.sprite = like0.eqSprite; blue.material = like0.eqMaterial; blue.color = like0.fillColor; }
                Image edge = Hints.Img(Hints.New("border", pickCarry.transform), Hints.Box(), BorderIdle);
                edge.type = Image.Type.Sliced;
                Hints.Place(edge.gameObject, Vector2.zero, Vector2.one, mid, Vector2.zero, Vector2.zero);
                pickCarryIcon = Hints.Img(Hints.New("icon", pickCarry.transform), null, Color.white);
                pickCarryIcon.preserveAspect = true;
                Hints.Place(pickCarryIcon.gameObject, mid, mid, mid, Vector2.zero, new Vector2(60f, 60f));   // the size an item has inside a square here
                pickCarryIcon2 = Hints.Img(Hints.New("icon2", pickCarry.transform), null, Color.white);
                pickCarryIcon2.preserveAspect = true;
                Hints.Place(pickCarryIcon2.gameObject, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-1f, 1f), new Vector2(39f, 39f));   // the shield of a set, tucked in the corner as on a square
            }

            // the holding slot: what sits in it, or the ghost of what was just lifted out of it
            holdFrame.color = onHold && focusFx == null ? Color.white : Color.clear;
            bool ghostHere = carrying && carryFrom == Home;
            string hm = ghostHere ? carryMain : holdMain, hs = ghostHere ? carryShield : holdShield;
            string first = hm ?? hs;
            float ha = ghostHere ? 0.3f : (carrying && onHold ? 0.25f : 1f);   // a ghost, or stepped back under the hand
            holdIcon.enabled = first != null;
            if (first != null) { holdIcon.sprite = IconOf(Find(p, first), first); holdIcon.color = new Color(1f, 1f, 1f, ha); }
            holdIcon2.enabled = hm != null && hs != null;
            if (holdIcon2.enabled) { holdIcon2.sprite = IconOf(Find(p, hs), hs); holdIcon2.color = new Color(1f, 1f, 1f, ha); }

            if (pickCarry.activeSelf != carrying) pickCarry.SetActive(carrying);
            if (carrying)
            {
                // A ghost stays on the square the thing came from, as an item lifted in the bag leaves one behind.
                if (carryFrom >= 0 && carryFrom < (two ? 8 : 4))
                {
                    Square from = carryFrom < 4 ? pickSquares[carryFrom] : pickSquares2[carryFrom - 4];
                    Color ghost = new Color(1f, 1f, 1f, 0.3f);
                    if (carryMain != null) { from.icon.enabled = true; from.icon.sprite = IconOf(Find(p, carryMain), carryMain); from.icon.color = ghost; }
                    if (carryShield != null) { from.icon2.enabled = true; from.icon2.sprite = IconOf(Find(p, carryShield), carryShield); from.icon2.color = ghost; }
                }
                // Over a square that already holds something, that something steps back, so the thing in hand reads clearly on top of it.
                if (at != null && pickFocus != carryFrom)
                {
                    Color c1 = at.icon.color; c1.a *= 0.25f; at.icon.color = c1;
                    Color c2 = at.icon2.color; c2.a *= 0.25f; at.icon2.color = c2;
                    at.count.alpha = 0.25f;
                }
                pickCarryIcon.sprite = IconOf(Find(p, carryMain ?? carryShield), carryMain ?? carryShield);
                bool pair = carryMain != null && carryShield != null;
                if (pickCarryIcon2.enabled != pair) pickCarryIcon2.enabled = pair;
                if (pair) pickCarryIcon2.sprite = IconOf(Find(p, carryShield), carryShield);
                // The thing in hand floats over whatever the focus is on, nudged a little up and to the right, the way a
                // carried item sits off its slot in the bag.
                float nudge = 12f * pickPanel.lossyScale.y;
                Vector3 over = onHold ? pickHome.transform.position : at != null ? at.border.transform.position : pickPanel.position;
                if (pickCursor) over = new Vector3(pickPos.x, pickPos.y, over.z);   // with the cursor up, it rides with the cursor
                pickCarry.transform.position = over + new Vector3(nudge, nudge, 0f);
                pickCarry.transform.SetAsLastSibling();
            }

            // the cursor itself: the same ring and dot as in the bag
            if (pickCursor && pickCursorRoot == null)
            {
                pickCursorRoot = Hints.New("cursor", pickPanel.transform);
                Hints.Place(pickCursorRoot, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
                Image shade = Hints.Img(Hints.New("shade", pickCursorRoot.transform), Hints.Ring(), new Color(0f, 0f, 0f, 0.7f));
                Hints.Place(shade.gameObject, mid, mid, mid, Vector2.zero, new Vector2(30f, 30f));
                Image ring = Hints.Img(Hints.New("ring", pickCursorRoot.transform), Hints.Ring(), Color.white);
                Hints.Place(ring.gameObject, mid, mid, mid, Vector2.zero, new Vector2(26f, 26f));
                Image dot = Hints.Img(Hints.New("dot", pickCursorRoot.transform), Hints.Disc(), Color.white);
                Hints.Place(dot.gameObject, mid, mid, mid, Vector2.zero, new Vector2(5f, 5f));
            }
            if (pickCursorRoot != null)
            {
                if (pickCursorRoot.activeSelf != pickCursor) pickCursorRoot.SetActive(pickCursor);
                if (pickCursor)
                {
                    pickCursorRoot.transform.position = new Vector3(pickPos.x, pickPos.y, pickCursorRoot.transform.position.z);
                    pickCursorRoot.transform.SetAsLastSibling();
                    CursorLook.Hover(pickCursorRoot, ref pickHoverK, pickOver && (carrying || (pickFocus >= Home ? holdMain != null || holdShield != null : main[pickFocus] != null || shield[pickFocus] != null)));   // over a square with something on it, or anywhere while carrying
                }
            }        }
    }
}