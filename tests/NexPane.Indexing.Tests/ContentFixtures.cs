using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MimeKit;
using OpenMcdf;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace NexPane.Indexing.Tests;

/// <summary>
/// The committed content fixtures under <c>tests/TestAssets/content/</c> (one per format, P3.31). They are produced by
/// <see cref="Regenerate"/> (run with <c>NEXPANE_REGENERATE_FIXTURES=1</c>) and read by the extractor tests.
/// </summary>
public static class ContentFixtures
{
    public const string Phrase = "invoice total";

    public static string Directory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexPane.sln")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found."), "tests", "TestAssets", "content");
        }
    }

    public static string PathOf(string file) => Path.Combine(Directory, file);

    [Fact]
    public static void Regenerate()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NEXPANE_REGENERATE_FIXTURES") == "1", "Fixtures are committed; set NEXPANE_REGENERATE_FIXTURES=1 to rebuild them.");
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(PathOf("utf8.txt"), "Café menu — the invoice total is 42 €.\nSecond line.", new UTF8Encoding(false));
        File.WriteAllText(PathOf("utf8-bom.txt"), "BOM text: Grüße aus Köln, invoice total 7.", new UTF8Encoding(true));
        File.WriteAllText(PathOf("utf16.txt"), "UTF-16 text: naïve résumé invoice total.", Encoding.Unicode);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(PathOf("cp1252.txt"), Encoding.GetEncoding(1252).GetBytes("Windows-1252: Smørrebrød costs 5 € in Århus."));
        File.WriteAllBytes(PathOf("binary.dat"), [0x4D, 0x5A, 0x00, 0x01, 0x02, 0x00, 0xFF]);
        WritePdf(PathOf("invoice.pdf"), ["ACME Corp", "Invoice 2026-001", "The invoice total is 1,234.56 EUR"]);
        WritePdf(PathOf("other.pdf"), ["Meeting notes", "Nothing about money here"]);
        WriteDocx(PathOf("letter.docx"), ["Dear customer,", "please find the invoice total attached.", "Kind regards"]);
        WriteXlsx(PathOf("budget.xlsx"));
        WritePptx(PathOf("deck.pptx"), "Quarterly review", "Revenue grew by twelve percent");
        WriteEml(PathOf("mail.eml"));
        WriteMsg(PathOf("mail.msg"));
        File.WriteAllText(PathOf("doc.rtf"), @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}\f0\fs24 Rich text says hello from the RTF IFilter.\par}", Encoding.ASCII);
    }

    private static void WritePdf(string path, string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var y = 750.0;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new PdfPoint(50, y), font);
            y -= 20;
        }

        File.WriteAllBytes(path, builder.Build());
    }

    private static void WriteDocx(string path, string[] paragraphs)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(paragraphs.Select(p => new W.Paragraph(new W.Run(new W.Text(p))))));
    }

    private static void WriteXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = doc.AddWorkbookPart();
        workbook.Workbook = new S.Workbook();
        var shared = workbook.AddNewPart<SharedStringTablePart>();
        shared.SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text("Office supplies")), new S.SharedStringItem(new S.Text("Travel budget")));
        var sheetPart = workbook.AddNewPart<WorksheetPart>();
        sheetPart.Worksheet = new S.Worksheet(new S.SheetData(
            new S.Row(
                new S.Cell { CellReference = "A1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") },
                new S.Cell { CellReference = "B1", CellValue = new S.CellValue("199.5") }),
            new S.Row(
                new S.Cell { CellReference = "A2", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("1") },
                new S.Cell { CellReference = "B2", CellValue = new S.CellValue("2400") })));
        workbook.Workbook.AppendChild(new S.Sheets(new S.Sheet { Id = workbook.GetIdOfPart(sheetPart), SheetId = 1, Name = "Budget" }));
    }

    private static void WritePptx(string path, string title, string body)
    {
        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentation = doc.AddPresentationPart();
        presentation.Presentation = new P.Presentation();
        var slidePart = presentation.AddNewPart<SlidePart>();
        slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(),
            Shape(2, title),
            Shape(3, body))));
        presentation.Presentation.AppendChild(new P.SlideIdList(new P.SlideId { Id = 256, RelationshipId = presentation.GetIdOfPart(slidePart) }));

        static P.Shape Shape(uint id, string text) => new(
            new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = id, Name = "Text " + id }, new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text(text)))));
    }

    private static void WriteEml(string path)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Billing", "billing@example.invalid"));
        message.To.Add(new MailboxAddress("Customer", "customer@example.invalid"));
        message.Subject = "Your monthly statement";
        message.Body = new TextPart("plain") { Text = "The invoice total for October is 88.00." };
        message.WriteTo(path);
    }

    private static void WriteMsg(string path)
    {
        using var root = RootStorage.Create(path);
        Write(root, "__substg1.0_001A001F", "IPM.Note");
        Write(root, "__substg1.0_0037001F", "Project kickoff");
        Write(root, "__substg1.0_0C1A001F", "Team Lead");
        Write(root, "__substg1.0_0E04001F", "Engineering");
        Write(root, "__substg1.0_1000001F", "Kickoff agenda: scope, milestones and the invoice total review.");
        using var properties = root.CreateStream("__properties_version1.0");
        properties.Write(new byte[32]);

        static void Write(RootStorage root, string name, string value)
        {
            using var stream = root.CreateStream(name);
            stream.Write(Encoding.Unicode.GetBytes(value + "\0"));
        }
    }
}
