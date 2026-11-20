namespace Lathe.Machine;

/// <summary>Plain-language text for grbl / grblHAL error and alarm codes.</summary>
public static class GrblCodes
{
    static readonly Dictionary<int, string> Errors = new()
    {
        [1] = "G-code words consist of a letter and a value. Letter was not found.",
        [2] = "Missing or invalid number in a G-code word.",
        [3] = "'$' system command not recognized or supported.",
        [4] = "Negative value received for a value that must be positive.",
        [5] = "Homing is not enabled in settings ($22).",
        [6] = "Minimum step pulse time must be greater than 3 µs.",
        [7] = "Settings read failed; defaults restored.",
        [8] = "'$' command only valid when the controller is Idle.",
        [9] = "G-code locked out during alarm or jog. Home ($H) or unlock ($X) first.",
        [10] = "Soft limits cannot be enabled without homing.",
        [11] = "Line too long (max characters per line exceeded).",
        [12] = "Setting value exceeds the maximum step rate.",
        [13] = "Safety door opened.",
        [14] = "Build info or startup line too long.",
        [15] = "Jog target exceeds machine travel.",
        [16] = "Invalid jog command (missing '=' or contains a forbidden G-code).",
        [17] = "Laser mode requires PWM output.",
        [20] = "Unsupported or invalid G-code command.",
        [21] = "More than one command from the same modal group in one line.",
        [22] = "Feed rate has not been set.",
        [23] = "G-code command requires an integer value.",
        [24] = "Two commands in one line both need the axis words.",
        [25] = "A G-code word was repeated in the line.",
        [26] = "Command requires axis words but none were given.",
        [27] = "Line number out of range.",
        [28] = "Command is missing a required P or L value.",
        [29] = "Unsupported work coordinate system.",
        [30] = "G53 requires G0 or G1 motion mode.",
        [31] = "Axis words given with no motion command active (G80).",
        [32] = "Arc has no axis words in the selected plane.",
        [33] = "Motion target is invalid (arc end point not on the circle, or similar).",
        [34] = "Arc radius/center definition error.",
        [35] = "Arc is missing its I/K center offset.",
        [36] = "Unused, leftover G-code words in the line.",
        [37] = "Tool length offset applied to the wrong axis.",
        [38] = "Tool number greater than supported.",
        [39] = "Value out of range.",
        [40] = "Tool change pending.",
        [41] = "Spindle is not running (required for feed-per-rev, CSS or threading).",
        [42] = "Illegal plane for this command.",
        [43] = "Maximum feed rate exceeded.",
        [44] = "Spindle RPM out of range.",
        [45] = "Limit switch engaged.",
        [46] = "Homing required before this command.",
        [47] = "Tool error.",
        [48] = "Value word conflict.",
        [49] = "Power-on self test failed.",
        [50] = "E-stop is active.",
        [51] = "Motor fault.",
        [52] = "Setting value out of range.",
        [53] = "Setting is disabled.",
        [54] = "Invalid retract position.",
        [55] = "Illegal homing configuration.",
        [56] = "Coordinate system is locked.",
        [60] = "SD card mount failed.",
        [61] = "SD card file open/read failed.",
        [62] = "SD card directory listing failed.",
        [63] = "SD card directory not found.",
        [64] = "SD card file empty.",
        [70] = "Bluetooth initialisation failed.",
        [79] = "Unexpected end of program/file.",
        [80] = "Expression/parameter error.",
    };

    static readonly Dictionary<int, string> Alarms = new()
    {
        [1] = "Hard limit triggered. Machine position is likely lost – re-home.",
        [2] = "Soft limit: the move target exceeds machine travel. Position retained.",
        [3] = "Reset while in motion. Position may be lost – re-home.",
        [4] = "Probe fail: probe not in expected initial state.",
        [5] = "Probe fail: probe did not contact.",
        [6] = "Homing fail: reset during homing.",
        [7] = "Homing fail: safety door opened during homing.",
        [8] = "Homing fail: pull-off did not clear the limit switch.",
        [9] = "Homing fail: limit switch not found.",
        [10] = "E-stop asserted. Clear the E-stop and reset.",
        [11] = "Homing required. Home the machine ($H) before running.",
        [12] = "Limit switch engaged. Clear it before continuing.",
        [13] = "Probe protection triggered.",
        [14] = "Spindle did not reach speed in time.",
        [15] = "Homing fail: second limit switch not found (auto-squared axis).",
        [16] = "Power-on self test failed.",
        [17] = "Motor fault.",
        [18] = "Homing fail: bad configuration.",
    };

    public static string Error(int code) =>
        Errors.TryGetValue(code, out var s) ? s : $"Unknown error {code}.";

    public static string Alarm(int code) =>
        Alarms.TryGetValue(code, out var s) ? s : $"Unknown alarm {code}.";
}
