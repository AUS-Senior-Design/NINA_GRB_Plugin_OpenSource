using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// [MANDATORY] The following GUID is used as a unique identifier of the plugin. Generate a fresh one for your plugin!
[assembly: Guid("0e071b8b-a73a-4e49-808a-54a9f59a9f22")]

// [MANDATORY] The assembly versioning
//Should be incremented for each new release build of a plugin
[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]

// [MANDATORY] The name of your plugin <- Do not change this
[assembly: AssemblyTitle("GRB_Helper")]
// [MANDATORY] A short description of your plugin
[assembly: AssemblyDescription("Automatically detects GRB alerts and schedules telescope capture based on observability.")]

// The following attributes are not required for the plugin per se, but are required by the official manifest meta data

// Your name
[assembly: AssemblyCompany("Ezzeddin, Halla , Insiyah, Qusai ")]
// The product name that this plugin is part of
[assembly: AssemblyProduct("Demo_2")]
[assembly: AssemblyCopyright("Copyright © 2026 SD")]

// The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.2017")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
// The repository where your pluggin is hosted
//[assembly: AssemblyMetadata("Repository", "https://github.com/AUS-Senior-Design/NINA_GRB_Plugin_OpenSource")]

// The following attributes are optional for the official manifest meta data

//[Optional] Your plugin homepage URL - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://github.com/AUS-Senior-Design/NINA_GRB_Plugin_OpenSource")]

//[Optional] Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "")]

//[Optional] A link that will show a log of all changes in between your plugin's versions
//[assembly: AssemblyMetadata("ChangelogURL", "https://mypluginsourcerepo.com/project/CHANGELOG.md")]

//[Optional] The url to a featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "")]
//[Optional] A url to an example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "")]
//[Optional] An additional url to an example example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
//[Optional] An in-depth description of your plugin
[assembly: AssemblyMetadata("LongDescription", @"This plugin enables automated follow-up observations of Gamma-Ray Bursts (GRBs) directly within N.I.N.A. by integrating real-time alert monitoring with observatory-aware scheduling.

The system continuously listens for GRB alerts from external sources (such as GCN/Firestore feeds), parses the incoming data, and evaluates each event based on user-defined scientific and observational constraints. These constraints include sky position (RA/Dec), uncertainty radius, brightness indicators (magnitude and flux), GRB age, and source telescope filters.

A key feature of the plugin is its observability engine, which determines whether a GRB is suitable for imaging from the user’s location. This evaluation takes into account the active N.I.N.A. profile’s geographic coordinates, altitude limits, twilight conditions, and moon constraints (phase, altitude, and angular separation).

When a GRB satisfies all configured criteria, the plugin can automatically prepare the observation workflow, enabling rapid-response astrophotography without manual intervention.

Additional capabilities include:
- Automatic injection of GRB-specific metadata into FITS headers (e.g., GRB name, coordinates, trigger time, flux, and magnitude), ensuring scientific traceability of captured images.
- Custom image file naming patterns for organizing GRB observations.
- Integration with N.I.N.A.'s profile system for seamless configuration management.
- Background monitoring service for continuous, real-time alert handling.

This plugin is designed for astrophotographers and researchers interested in time-critical transient events, providing a bridge between professional alert systems and amateur observatory automation.
")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]
// [Unused]
[assembly: AssemblyConfiguration("")]
// [Unused]
[assembly: AssemblyTrademark("")]
// [Unused]
[assembly: AssemblyCulture("")]