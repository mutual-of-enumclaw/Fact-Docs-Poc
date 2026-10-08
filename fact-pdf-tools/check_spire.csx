#r "C:\src\fact-pdf-tools\demo\bin\Debug\net9.0\Spire.Pdf.dll"
using Spire.Pdf.Texts;
var t = typeof(PdfTextLine);
foreach (var p in t.GetProperties()) Console.WriteLine(p.Name + ": " + p.PropertyType.Name);
foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) Console.WriteLine("M: " + m.Name);
