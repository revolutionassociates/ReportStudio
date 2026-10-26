namespace ReportStudio.Core.Data;

using Row = Dictionary<string, object?>;

/// <summary>Deterministic demo data so the designer and viewer work without a database.</summary>
public static class SampleData
{
    public static readonly string[] FieldNames =
        { "Country", "Customer", "InvoiceNo", "InvoiceDate", "Product", "Quantity", "UnitPrice", "Amount" };

    public static List<Row> Generate()
    {
        var rnd = new Random(42);
        var countries = new[] { "India", "Germany", "United States" };
        var customers = new Dictionary<string, string[]>
        {
            ["India"] = new[] { "Sharma Traders", "Kaveri Textiles" },
            ["Germany"] = new[] { "Berg GmbH", "Hansa Logistik" },
            ["United States"] = new[] { "Northwind Supply", "Lakeside Foods" }
        };
        var products = new (string Name, decimal Price)[]
        {
            ("Industrial Fan", 120m), ("Safety Helmet", 18.5m), ("Cable Drum", 64m),
            ("LED Panel", 42.75m), ("Hand Pallet Truck", 310m), ("Gloves (box)", 9.9m)
        };

        var rows = new List<Row>();
        int invoice = 1000;
        var start = new DateTime(2026, 1, 5);
        foreach (var country in countries)
            foreach (var customer in customers[country])
                for (int n = 0, invoiceCount = 2 + rnd.Next(0, 3); n < invoiceCount; n++)
                {
                    invoice++;
                    var date = start.AddDays(rnd.Next(0, 280));
                    for (int line = 0, lineCount = 1 + rnd.Next(0, 3); line < lineCount; line++)
                    {
                        var (name, price) = products[rnd.Next(products.Length)];
                        var qty = (decimal)(1 + rnd.Next(0, 20));
                        rows.Add(new Row(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Country"] = country,
                            ["Customer"] = customer,
                            ["InvoiceNo"] = (decimal)invoice,
                            ["InvoiceDate"] = date,
                            ["Product"] = name,
                            ["Quantity"] = qty,
                            ["UnitPrice"] = price,
                            ["Amount"] = qty * price
                        });
                    }
                }
        return rows;
    }
}
