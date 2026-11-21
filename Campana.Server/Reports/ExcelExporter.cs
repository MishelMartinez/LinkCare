using Campana.Core;
using ClosedXML.Excel;

namespace Campana.Server.Reports;

/// <summary>Excel para patrocinadores y gobierno. Solo lleva cifras agregadas: ningún nombre ni dato personal.</summary>
public static class ExcelExporter
{
    public static byte[] Build(IReadOnlyList<CampaignStats> campaigns, DateTimeOffset now)
    {
        using var wb = new XLWorkbook();

        var s = wb.Worksheets.Add("Resumen");
        s.Cell(1, 1).Value = "Resumen de campañas de detección oportuna de cáncer de mama y cervicouterino";
        s.Cell(1, 1).Style.Font.Bold = true;
        s.Cell(2, 1).Value = $"Generado: {now:yyyy-MM-dd HH:mm} UTC. Cifras agregadas, sin datos personales. Los casos sospechosos son hallazgos de alerta del tamizaje (mama: BI-RADS 4 o 5; Papanicolaou: marcado sospechoso), no diagnósticos.";
        string[] heads =
        {
            "Campaña", "Lugar", "Unidad geográfica", "Etapa", "Pacientes registradas", "Pacientes atendidas", "Nuevas", "Recurrentes",
            "Visitas", "Estudios", "Estudios de mama", "Estudios de Papanicolaou", "Casos sospechosos", "Pacientes con caso sospechoso", "Casos para seguimiento", "Adjuntos",
        };
        for (var i = 0; i < heads.Length; i++) s.Cell(4, i + 1).Value = heads[i];
        var r = 5;
        foreach (var c in campaigns)
        {
            object[] row = { c.CampaignId, c.Lugar, c.UnidadGeografica, c.Estado, c.PacientesRegistradas, c.PacientesAtendidas, c.PacientesNuevas, c.PacientesRecurrentes,
                c.Visitas, c.Estudios, c.EstudiosMama, c.EstudiosPapanicolaou, c.CasosSospechosos, c.PacientesConCasoSospechoso, c.CasosSeguimiento, c.Adjuntos };
            for (var i = 0; i < row.Length; i++) SetCell(s.Cell(r, i + 1), row[i]);
            r++;
        }
        s.Cell(r, 1).Value = "Total";
        for (var col = 5; col <= heads.Length; col++)
            s.Cell(r, col).FormulaA1 = $"SUM({s.Cell(5, col).Address}:{s.Cell(Math.Max(5, r - 1), col).Address})";
        s.Row(r).Style.Font.Bold = true;
        Header(s.Range(4, 1, 4, heads.Length));
        s.Columns().AdjustToContents();
        s.Column(1).Width = 36;
        s.Row(2).Style.Alignment.WrapText = false;

        var k = wb.Worksheets.Add("Consultorios");
        string[] kh = { "Campaña", "Consultorio", "Nombre", "Visitas", "Estudios" };
        for (var i = 0; i < kh.Length; i++) k.Cell(1, i + 1).Value = kh[i];
        r = 2;
        foreach (var c in campaigns)
            foreach (var cs in c.PorConsultorio)
            {
                k.Cell(r, 1).Value = c.CampaignId; k.Cell(r, 2).Value = cs.ConsultorioId; k.Cell(r, 3).Value = cs.Nombre;
                k.Cell(r, 4).Value = cs.Visitas; k.Cell(r, 5).Value = cs.Estudios; r++;
            }
        Header(k.Range(1, 1, 1, kh.Length));
        k.Columns().AdjustToContents();

        var d = wb.Worksheets.Add("Por día");
        string[] dh = { "Campaña", "Fecha", "Visitas", "Estudios" };
        for (var i = 0; i < dh.Length; i++) d.Cell(1, i + 1).Value = dh[i];
        r = 2;
        foreach (var c in campaigns)
            foreach (var day in c.PorDia)
            {
                d.Cell(r, 1).Value = c.CampaignId; d.Cell(r, 2).Value = day.Fecha.ToDateTime(TimeOnly.MinValue);
                d.Cell(r, 2).Style.DateFormat.Format = "yyyy-mm-dd";
                d.Cell(r, 3).Value = day.Visitas; d.Cell(r, 4).Value = day.Estudios; r++;
            }
        Header(d.Range(1, 1, 1, dh.Length));
        d.Columns().AdjustToContents();

        var b = wb.Worksheets.Add("BI-RADS");
        b.Cell(1, 1).Value = "Campaña";
        for (var i = 0; i < StatsCalculator.BiradsKeys.Length; i++)
            b.Cell(1, i + 2).Value = StatsCalculator.BiradsKeys[i] == "sin_dato" ? "Sin dato" : $"BI-RADS {StatsCalculator.BiradsKeys[i]}";
        r = 2;
        foreach (var c in campaigns)
        {
            b.Cell(r, 1).Value = c.CampaignId;
            for (var i = 0; i < StatsCalculator.BiradsKeys.Length; i++)
                b.Cell(r, i + 2).Value = c.PorBirads.TryGetValue(StatsCalculator.BiradsKeys[i], out var n) ? n : 0;
            r++;
        }
        Header(b.Range(1, 1, 1, StatsCalculator.BiradsKeys.Length + 1));
        b.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void SetCell(IXLCell cell, object value)
    {
        switch (value)
        {
            case int i: cell.Value = i; break;
            default: cell.Value = value?.ToString() ?? ""; break;
        }
    }

    private static void Header(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.LightGray;
    }
}
