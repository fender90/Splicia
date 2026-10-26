namespace Splicia;

/// <summary>Product, publisher and license details shown in the app, the setup wizard and Settings → Apps.</summary>
static class AppInfo
{
    public const string Name = "Splicia";
    public const string Tagline = "Trim, rotate, crop and export videos";
    public const string Publisher = "Vesperis Labs";
    public const string Website = "https://www.vesperislabs.com";
    public const string WebsiteLabel = "www.vesperislabs.com";
    public const string License = "CC BY 4.0";
    public const string LicenseName = "Creative Commons Attribution 4.0 International";
    public const string LicenseUrl = "https://creativecommons.org/licenses/by/4.0/";
    public const string Copyright = "© 2026 " + Publisher;

    /// <summary>Files shipped next to the exe (embedded, written out by the installer).</summary>
    public const string LicenseFile = "LICENSE.txt", NoticesFile = "THIRD-PARTY-NOTICES.txt";

    public static string Version => Application.ProductVersion.Split('+')[0];
}
