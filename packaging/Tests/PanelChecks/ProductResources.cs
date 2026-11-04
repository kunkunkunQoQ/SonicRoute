using System.Linq;
using System.Reflection;
using System.Windows;
using System.Xml.Linq;

internal static partial class Program
{
    private static ResourceDictionary ProductResources()
    {
        // Reuse product resources with startup suppressed, without constructing a second Application.
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductApp.xaml")!;
        var document = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = document.Root!.Element(presentation + "Application.Resources")!.Elements().Single();
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        dictionary.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:SonicRoute;assembly=SonicRoute");
        foreach (var element in dictionary.Descendants().Where(element => element.Name.NamespaceName == "clr-namespace:SonicRoute"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:SonicRoute;assembly=SonicRoute");
        foreach (var source in dictionary.Descendants(presentation + "ResourceDictionary").Attributes("Source"))
            source.Value = "/SonicRoute;component/" + source.Value;
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
    }
}
