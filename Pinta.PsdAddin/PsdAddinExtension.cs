using Pinta.Core;

[assembly: Mono.Addins.Addin ("PsdToolsImporter", PintaCore.ApplicationVersion, Category = "FileFormats")]
[assembly: Mono.Addins.AddinName ("PSD Tools Importer")]
[assembly: Mono.Addins.AddinDescription ("Registers PSD import support through an external psd-tools helper.")]
[assembly: Mono.Addins.AddinDependency ("Pinta", PintaCore.ApplicationVersion)]
[assembly: Mono.Addins.AddinFlags (Mono.Addins.Description.AddinFlags.Hidden | Mono.Addins.Description.AddinFlags.CantUninstall)]

namespace Pinta.PsdAddin;

[Mono.Addins.Extension]
public sealed class PsdAddinExtension : IExtension
{
	private static readonly FormatDescriptor psd_format = new (
		displayPrefix: "Photoshop",
		extensions: ["psd", "PSD"],
		mimes: ["image/vnd.adobe.photoshop"],
		importer: new PsdToolsImporter (),
		exporter: null,
		supportsLayers: true);

	public void Initialize ()
	{
		PintaCore.ImageFormats.RegisterFormat (psd_format);
	}

	public void Uninitialize ()
	{
		PintaCore.ImageFormats.UnregisterFormatByExtension ("psd");
	}
}
