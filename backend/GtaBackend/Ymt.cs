using System.Xml;
using CodeWalker.GameFiles;

namespace GtaBackend;

/// <summary>YMT (CPedVariationInfo) ⇄ XML über CodeWalker.</summary>
public static class Ymt
{
    public static string ToXml(byte[] data, string fileName)
    {
        var ymt = new YmtFile();
        ymt.Load(data); // RSC7 mit Kopf, komprimiert (wie aus FiveM-Resources)
        ymt.Name = fileName;
        var xml = MetaXml.GetXml(ymt, out _);
        if (string.IsNullOrEmpty(xml)) throw new UserError($"{fileName} konnte nicht gelesen werden.");
        return xml;
    }

    public static byte[] FromXml(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var data = XmlMeta.GetData(doc, MetaFormat.RSC, "");
        if (data == null || data.Length < 16) throw new UserError("Die YMT konnte nicht erzeugt werden.");
        return data;
    }

    public static object Roundtrip(string file, string outXml)
    {
        var xml = ToXml(File.ReadAllBytes(file), Path.GetFileName(file));
        if (!string.IsNullOrEmpty(outXml)) File.WriteAllText(outXml, xml);
        var back = FromXml(xml);
        var xml2 = ToXml(back, Path.GetFileName(file));
        return new { size = new FileInfo(file).Length, rebuilt = back.Length, sameXml = xml == xml2, xmlLength = xml.Length };
    }
}
