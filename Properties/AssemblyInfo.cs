using System.Reflection;
using System.Runtime.InteropServices;

// ── Basic assembly metadata ───────────────────────────────────────────────────
[assembly: AssemblyTitle("Seestar Cable Wrap Monitor")]
[assembly: AssemblyDescription("Tracks RA axis rotation on the ZWO Seestar S50 to prevent USB cable wrap during remote imaging sessions.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("CableWrapMonitor")]
[assembly: AssemblyCopyright("Copyright © Adam 2025")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]

// Unique identifier for this plugin — do NOT change once deployed
[assembly: Guid("4a7b8c2d-1e3f-4956-b8c1-d2e3f4a56789")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// ── NINA Plugin metadata ──────────────────────────────────────────────────────
// These are read by NINA's plugin loader and shown in the Plugin Manager.

[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
[assembly: AssemblyMetadata("MaximumApplicationVersion", "99.0.0.0")]

[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Repository", "")]
[assembly: AssemblyMetadata("Tags", "cable-wrap,seestar,equipment,safety")]

[assembly: AssemblyMetadata("FeaturedImageURL", "")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]

[assembly: AssemblyMetadata("LongDescription", @"## Seestar Cable Wrap Monitor

Tracks the cumulative RA axis rotation of your ZWO Seestar S50 telescope to detect
and warn about USB cable wrap before it becomes a problem during remote imaging sessions.

### Features
- Polls the telescope's Right Ascension value every 5 seconds
- Accumulates total rotation in both directions (unwinding counts back toward zero)
- Warns you when the cable has wrapped beyond your configured threshold (default: 1 full rotation)
- Logs a timestamped history of each 360° threshold crossing
- Persists all state across NINA restarts — safe for remote sessions
- Includes a 'Check Cable Wrap' sequence instruction for automated sequence pausing
- Detects slews and ignores them so they don't count as cable movement

### Setup
1. Connect your Seestar S50 via the ZWO ALPACA driver in NINA
2. Open the Cable Wrap Monitor panel (View → Dockable Panels)
3. Optionally add a 'Check Cable Wrap' instruction to your imaging sequence
4. Click 'Reset — Cable Unwound' after physically unwinding the cable")]
