using System.Text;

namespace BDArmory.Avionics
{
    /// <summary>
    /// Data link terminal, added by ModuleManager to command parts and BDArmory weapon managers.
    /// Set up in the VAB/SPH (part menu or the C4ISR editor window): role, data link, IADS membership and EMCON override.
    /// A craft with no terminal still joins the network with automatic settings.
    /// </summary>
    public class ModuleC4ISRTerminal : PartModule
    {
        public static readonly string[] Roles = ["Auto", "Fighter", "AEW", "SAM", "EWR", "HQ", "Ground", "Ship"];
        public static readonly string[] EmconModes = ["Team", "Always on", "Silent"];

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "C4ISR role", groupName = "c4isr", groupDisplayName = "C4ISR data link", groupStartCollapsed = true),
         UI_ChooseOption(options = ["Auto", "Fighter", "AEW", "SAM", "EWR", "HQ", "Ground", "Ship"], scene = UI_Scene.All)]
        public string role = "Auto";

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Data link", groupName = "c4isr"),
         UI_Toggle(enabledText = "On", disabledText = "Off", scene = UI_Scene.All)]
        public bool dataLink = true;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "IADS member", groupName = "c4isr"),
         UI_Toggle(enabledText = "Yes", disabledText = "No", scene = UI_Scene.All)]
        public bool iadsMember = true;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Radar EMCON", groupName = "c4isr"),
         UI_ChooseOption(options = ["Team", "Always on", "Silent"], scene = UI_Scene.All)]
        public string emcon = "Team";

        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "Network", groupName = "c4isr")]
        public string status = "-";

        public bool HasRoleOverride => role != "Auto" && System.Enum.TryParse(role, out NodeRole _);
        public NodeRole RoleOverride => System.Enum.TryParse(role, out NodeRole r) ? r : NodeRole.Fighter;

        /// <summary>The terminal that speaks for a vessel: the one on the weapon manager's part if there is one, else the first found.</summary>
        public static ModuleC4ISRTerminal For(Vessel v, Part wmPart = null)
        {
            if (v == null) return null;
            if (wmPart != null)
            {
                var t = wmPart.FindModuleImplementing<ModuleC4ISRTerminal>();
                if (t != null) return t;
            }
            foreach (var p in v.parts)
            {
                var t = p != null ? p.FindModuleImplementing<ModuleC4ISRTerminal>() : null;
                if (t != null) return t;
            }
            return null;
        }

        public override string GetInfo()
        {
            var s = new StringBuilder();
            s.AppendLine("Shares this craft's radar and IRST tracks with its team over the data link, and takes C2 and IADS tasking.");
            s.AppendLine();
            s.AppendLine("<b>Set in the part menu or the C4ISR editor window:</b>");
            s.AppendLine("- Role (Auto works it out from weapons and sensors)");
            s.AppendLine("- Data link on/off");
            s.AppendLine("- IADS member");
            s.AppendLine("- Radar EMCON override");
            return s.ToString();
        }

        public override string GetModuleDisplayName() => "C4ISR Data Link Terminal";
    }
}
