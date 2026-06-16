using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AfipSDK.Afip.Net;

LoadDotEnv();
CheckEnvs();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = "public"
});
builder.WebHost.UseUrls("http://localhost:4719");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
});
builder.Services.AddSingleton(_ => new Afip(AfipOptionsFactory.FromEnvironment()));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/bill", async (BillRequest request, Afip afip) =>
{
    try
    {
        var tipoDeFactura = 6; // Factura B
        var lastVoucher = await afip.ElectronicBilling.GetLastVoucherAsync(request.PuntoDeVenta, tipoDeFactura);
        var voucherInfo = lastVoucher > 0
            ? await afip.ElectronicBilling.GetVoucherInfoAsync(lastVoucher, request.PuntoDeVenta, tipoDeFactura)
            : null;

        var numeroDeFactura = lastVoucher + 1;
        var importeTotal = request.ImporteGravado + request.ImporteIva + request.ImporteExentoIva;
        var fecha = Math.Max(GetDictionaryInt(voucherInfo, "CbteFch") ?? 0, DateHelpers.GetTodayAsNumber());

        var voucherData = new Dictionary<string, object?>
        {
            ["CantReg"] = 1,
            ["PtoVta"] = request.PuntoDeVenta,
            ["CbteTipo"] = tipoDeFactura,
            ["Concepto"] = request.Concepto,
            ["DocTipo"] = request.TipoDeDocumento,
            ["DocNro"] = request.NumeroDeDocumento,
            ["CbteDesde"] = numeroDeFactura,
            ["CbteHasta"] = numeroDeFactura,
            ["CbteFch"] = fecha,
            ["FchServDesde"] = request.FechaServicioDesde,
            ["FchServHasta"] = request.FechaServicioHasta,
            ["FchVtoPago"] = request.FechaVencimientoPago,
            ["ImpTotal"] = importeTotal,
            ["ImpTotConc"] = 0,
            ["ImpNeto"] = request.ImporteGravado,
            ["ImpOpEx"] = request.ImporteExentoIva,
            ["ImpIVA"] = request.ImporteIva,
            ["ImpTrib"] = 0,
            ["MonId"] = "PES",
            ["MonCotiz"] = 1,
            ["CondicionIVAReceptorId"] = request.CondicionIvaReceptor,
            ["Iva"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Id"] = 5,
                    ["BaseImp"] = request.ImporteGravado,
                    ["Importe"] = request.ImporteIva
                }
            }
        };

        var billResponse = await afip.ElectronicBilling.CreateVoucherAsync(voucherData);
        var cae = GetDictionaryString(billResponse, "CAE") ?? string.Empty;
        var caeVencimiento = GetDictionaryString(billResponse, "CAEFchVto") ?? string.Empty;

        var pdfResponse = await afip.ElectronicBilling.CreatePDFAsync(CreatePdfRequest(
            afip.Options.CUIT ?? string.Empty,
            request,
            numeroDeFactura,
            fecha,
            importeTotal,
            cae,
            caeVencimiento
        ));

        return Results.Json(new PdfResponse(pdfResponse.File, pdfResponse.FileName), JsonOptions.Default);
    }
    catch (AfipWebServiceException ex)
    {
        return Results.Json(new { message = ex.Message }, JsonOptions.Default, statusCode: 400);
    }
    catch (HttpRequestException ex)
    {
        return Results.Json(new { message = ex.Message }, JsonOptions.Default, statusCode: 400);
    }
    catch (Exception ex)
    {
        return Results.Json(new { message = ex.Message }, JsonOptions.Default, statusCode: 500);
    }
});

app.Run();

static CreatePDFRequest CreatePdfRequest(
    string cuit,
    BillRequest request,
    int numeroDeFactura,
    int fecha,
    decimal importeTotal,
    string cae,
    string caeVencimiento)
{
    object issuerCuit = long.TryParse(cuit, CultureInfo.InvariantCulture, out var parsedCuit)
        ? parsedCuit
        : cuit;

    var templateParams = new Dictionary<string, object>
    {
        ["voucher_number"] = numeroDeFactura,
        ["sales_point"] = request.PuntoDeVenta,
        ["issue_date"] = DateHelpers.FormatDateNumber(fecha),
        ["cae_due_date"] = DateHelpers.FormatIsoDateForDisplay(caeVencimiento),
        ["issuer_cuit"] = issuerCuit,
        ["cae"] = cae,
        ["issuer_business_name"] = "Empresa imaginaria S.A.",
        ["issuer_address"] = "Calle falsa 123",
        ["issuer_iva_condition"] = "Responsable inscripto",
        ["issuer_gross_income"] = cuit,
        ["issuer_activity_start_date"] = DateHelpers.FormatDateNumber(fecha),
        ["receiver_name"] = "Consumidor Final",
        ["receiver_address"] = "-",
        ["receiver_document_type"] = 99,
        ["receiver_document_number"] = request.NumeroDeDocumento,
        ["receiver_iva_condition"] = request.CondicionIvaReceptor.ToString(CultureInfo.InvariantCulture),
        ["sale_condition"] = "Contado",
        ["currency_id"] = "ARS",
        ["currency_rate"] = 1,
        ["concept"] = 1,
        ["items"] = new[]
        {
            new Dictionary<string, object>
            {
                ["code"] = "001",
                ["description"] = "Servicio",
                ["quantity"] = 1,
                ["unit_price"] = importeTotal,
                ["subtotal"] = importeTotal
            }
        },
        ["vat_amount"] = request.ImporteIva,
        ["tributes_amount"] = 0,
        ["total_amount"] = importeTotal
    };

    if (request.FechaServicioDesde is not null)
    {
        templateParams["billing_from"] = DateHelpers.FormatDateNumber(request.FechaServicioDesde.Value);
    }

    if (request.FechaServicioHasta is not null)
    {
        templateParams["billing_to"] = DateHelpers.FormatDateNumber(request.FechaServicioHasta.Value);
    }

    if (request.FechaVencimientoPago is not null)
    {
        templateParams["payment_due_date"] = DateHelpers.FormatDateNumber(request.FechaVencimientoPago.Value);
    }

    return new CreatePDFRequest
    {
        FileName = $"factura-b-{numeroDeFactura.ToString().PadLeft(8, '0')}.pdf",
        Template = new Dictionary<string, object>
        {
            ["name"] = "invoice-b",
            ["params"] = templateParams
        }
    };
}

static void CheckEnvs()
{
    var hasCertPath = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AFIP_CERT_PATH"));
    var hasKeyPath = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AFIP_KEY_PATH"));

    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AFIP_CUIT")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AFIP_ACCESS_TOKEN")) ||
        hasCertPath != hasKeyPath)
    {
        Console.Error.WriteLine("ERROR: Falta configurar variables de ambiente revise el README para mas informacion.");
        Environment.Exit(1);
    }
}

static void LoadDotEnv()
{
    var path = Path.Combine(AppContext.BaseDirectory, ".env");
    if (!File.Exists(path))
    {
        path = Path.Combine(Directory.GetCurrentDirectory(), ".env");
    }

    if (!File.Exists(path))
    {
        return;
    }

    foreach (var rawLine in File.ReadAllLines(path))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            continue;
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim().Trim('"');
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

static int? GetDictionaryInt(Dictionary<string, object?>? data, string key)
{
    if (data is null || !data.TryGetValue(key, out var value))
    {
        return null;
    }

    return value switch
    {
        int number => number,
        long number => checked((int)number),
        decimal number => checked((int)number),
        JsonElement { ValueKind: JsonValueKind.Number } element => element.GetInt32(),
        JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var number) => number,
        string text when int.TryParse(text, out var number) => number,
        _ => null
    };
}

static string? GetDictionaryString(Dictionary<string, object?> data, string key)
{
    if (!data.TryGetValue(key, out var value))
    {
        return null;
    }

    return value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement element => element.ToString(),
        _ => value?.ToString()
    };
}

static class AfipOptionsFactory
{
    public static AfipOptions FromEnvironment()
    {
        var certPath = Environment.GetEnvironmentVariable("AFIP_CERT_PATH");
        var keyPath = Environment.GetEnvironmentVariable("AFIP_KEY_PATH");
        var production = string.Equals(Environment.GetEnvironmentVariable("AFIP_PRODUCTION"), "true", StringComparison.OrdinalIgnoreCase);

        return new AfipOptions
        {
            CUIT = Environment.GetEnvironmentVariable("AFIP_CUIT"),
            AccessToken = Environment.GetEnvironmentVariable("AFIP_ACCESS_TOKEN"),
            Cert = string.IsNullOrWhiteSpace(certPath) ? null : File.ReadAllText(certPath),
            Key = string.IsNullOrWhiteSpace(keyPath) ? null : File.ReadAllText(keyPath),
            Production = production
        };
    }
}

sealed record BillRequest(
    [property: JsonPropertyName("numero_de_documento")] long NumeroDeDocumento,
    [property: JsonPropertyName("tipo_de_documento")] int TipoDeDocumento,
    [property: JsonPropertyName("importe_gravado")] decimal ImporteGravado,
    [property: JsonPropertyName("importe_exento_iva")] decimal ImporteExentoIva,
    [property: JsonPropertyName("importe_iva")] decimal ImporteIva,
    [property: JsonPropertyName("punto_de_venta")] int PuntoDeVenta,
    [property: JsonPropertyName("concepto")] int Concepto,
    [property: JsonPropertyName("condicion_iva_receptor")] int CondicionIvaReceptor,
    [property: JsonPropertyName("fecha_servicio_desde")] int? FechaServicioDesde,
    [property: JsonPropertyName("fecha_servicio_hasta")] int? FechaServicioHasta,
    [property: JsonPropertyName("fecha_vencimiento_pago")] int? FechaVencimientoPago
);

sealed record PdfResponse(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("file_name")] string FileName
);

static class DateHelpers
{
    public static int GetTodayAsNumber()
    {
        return int.Parse(DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    public static string FormatDateNumber(int dateNumber)
    {
        var text = dateNumber.ToString(CultureInfo.InvariantCulture);
        return $"{text[6..8]}/{text[4..6]}/{text[0..4]}";
    }

    public static string FormatIsoDateForDisplay(string isoDate)
    {
        var parts = isoDate.Split('-');
        return parts.Length == 3 ? $"{parts[2]}/{parts[1]}/{parts[0]}" : isoDate;
    }
}

static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
