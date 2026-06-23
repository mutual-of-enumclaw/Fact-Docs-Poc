using System;
using System.IO;
using System.Drawing.Imaging;
using Spire.Pdf;

var pdf = new PdfDocument();
pdf.LoadFromBytes(System.IO.File.ReadAllBytes(@"C:\src\fact-pdf-tools\output\CA2146_populated.pdf"));
var methods = pdf.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
foreach (var m in methods) {
    if (m.Name.Contains("Image") || m.Name.Contains("image") || m.Name.Contains("Save"))
        Console.WriteLine($"{m.ReturnType.Name} {m.Name}({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name))})");
}