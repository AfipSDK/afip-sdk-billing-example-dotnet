using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

LoadDotEnv();
CheckEnvs();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:4719");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
});
builder.Services.AddSingleton<AfipSdkOptions>(_ => AfipSdkOptions.FromEnvironment());
builder.Services.AddHttpClient<AfipSdkClient>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/bill", async (BillRequest request, AfipSdkClient afip) =>
{
    try
    {
        var tipoDeFactura = 6; // Factura B
        var lastVoucher = await afip.GetLastVoucherAsync(request.PuntoDeVenta, tipoDeFactura);
        var voucherInfo = await afip.GetVoucherInfoAsync(lastVoucher, request.PuntoDeVenta, tipoDeFactura);

        var numeroDeFactura = lastVoucher + 1;
        var importeTotal = request.ImporteGravado + request.ImporteIva + request.ImporteExentoIva;
        var fecha = Math.Max(voucherInfo?.CbteFch ?? 0, DateHelpers.GetTodayAsNumber());

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

        var billResponse = await afip.CreateVoucherAsync(voucherData);
        var pdfResponse = await afip.CreatePdfAsync(new PdfInput(
            request,
            numeroDeFactura,
            fecha,
            importeTotal,
            billResponse.Cae,
            billResponse.CaeFchVto
        ));

        return Results.Json(pdfResponse, JsonOptions.Default);
    }
    catch (AfipSdkException ex)
    {
        return Results.Json(new { message = ex.Message }, JsonOptions.Default, statusCode: 400);
    }
    catch (Exception ex)
    {
        return Results.Json(new { message = ex.Message }, JsonOptions.Default, statusCode: 500);
    }
});

app.Run();

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

sealed record AfipSdkOptions(
    long Cuit,
    string AccessToken,
    string? Cert,
    string? Key,
    bool Production)
{
    public static AfipSdkOptions FromEnvironment()
    {
        var cuit = long.Parse(Environment.GetEnvironmentVariable("AFIP_CUIT")!, CultureInfo.InvariantCulture);
        var accessToken = Environment.GetEnvironmentVariable("AFIP_ACCESS_TOKEN")!;
        var certPath = Environment.GetEnvironmentVariable("AFIP_CERT_PATH");
        var keyPath = Environment.GetEnvironmentVariable("AFIP_KEY_PATH");
        var production = string.Equals(Environment.GetEnvironmentVariable("AFIP_PRODUCTION"), "true", StringComparison.OrdinalIgnoreCase);

        return new AfipSdkOptions(
            cuit,
            accessToken,
            string.IsNullOrWhiteSpace(certPath) ? null : File.ReadAllText(certPath),
            string.IsNullOrWhiteSpace(keyPath) ? null : File.ReadAllText(keyPath),
            production
        );
    }
}

sealed class AfipSdkClient
{
    private const string ApiBaseUrl = "https://app.afipsdk.com/api/";
    private readonly HttpClient _httpClient;
    private readonly AfipSdkOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = JsonOptions.Default;

    public AfipSdkClient(HttpClient httpClient, AfipSdkOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _httpClient.BaseAddress = new Uri(ApiBaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        _httpClient.DefaultRequestHeaders.Add("sdk-version-number", "1.0.0");
        _httpClient.DefaultRequestHeaders.Add("sdk-library", "dotnet");
        _httpClient.DefaultRequestHeaders.Add("sdk-environment", options.Production ? "prod" : "dev");
    }

    public async Task<int> GetLastVoucherAsync(int salesPoint, int type)
    {
        var result = await ExecuteElectronicBillingRequestAsync("FECompUltimoAutorizado", new Dictionary<string, object?>
        {
            ["PtoVta"] = salesPoint,
            ["CbteTipo"] = type
        });

        return result.GetProperty("CbteNro").GetInt32();
    }

    public async Task<VoucherInfo?> GetVoucherInfoAsync(int number, int salesPoint, int type)
    {
        if (number <= 0)
        {
            return null;
        }

        try
        {
            var result = await ExecuteElectronicBillingRequestAsync("FECompConsultar", new Dictionary<string, object?>
            {
                ["FeCompConsReq"] = new Dictionary<string, object?>
                {
                    ["CbteNro"] = number,
                    ["PtoVta"] = salesPoint,
                    ["CbteTipo"] = type
                }
            });

            return result.TryGetProperty("ResultGet", out var resultGet) && resultGet.ValueKind != JsonValueKind.Null
                ? JsonSerializer.Deserialize<VoucherInfo>(resultGet.GetRawText(), _jsonOptions)
                : null;
        }
        catch (AfipSdkException ex) when (ex.Code == 602)
        {
            return null;
        }
    }

    public async Task<CreateVoucherResponse> CreateVoucherAsync(Dictionary<string, object?> data)
    {
        var cbteDesde = Convert.ToInt32(data["CbteDesde"], CultureInfo.InvariantCulture);
        var cbteHasta = Convert.ToInt32(data["CbteHasta"], CultureInfo.InvariantCulture);
        var ptoVta = Convert.ToInt32(data["PtoVta"], CultureInfo.InvariantCulture);
        var cbteTipo = Convert.ToInt32(data["CbteTipo"], CultureInfo.InvariantCulture);

        var detail = data
            .Where(entry => entry.Key is not "CantReg" and not "PtoVta" and not "CbteTipo")
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        if (detail.TryGetValue("Iva", out var iva) && iva is not null)
        {
            detail["Iva"] = new Dictionary<string, object?> { ["AlicIva"] = iva };
        }

        var request = new Dictionary<string, object?>
        {
            ["FeCAEReq"] = new Dictionary<string, object?>
            {
                ["FeCabReq"] = new Dictionary<string, object?>
                {
                    ["CantReg"] = cbteHasta - cbteDesde + 1,
                    ["PtoVta"] = ptoVta,
                    ["CbteTipo"] = cbteTipo
                },
                ["FeDetReq"] = new Dictionary<string, object?>
                {
                    ["FECAEDetRequest"] = detail
                }
            }
        };

        var result = await ExecuteElectronicBillingRequestAsync("FECAESolicitar", request);
        var detailResponse = result
            .GetProperty("FeDetResp")
            .GetProperty("FECAEDetResponse");

        if (detailResponse.ValueKind == JsonValueKind.Array)
        {
            detailResponse = detailResponse[0];
        }

        return new CreateVoucherResponse(
            detailResponse.GetProperty("CAE").GetString() ?? string.Empty,
            DateHelpers.FormatAfipDateAsIso(detailResponse.GetProperty("CAEFchVto").GetRawText().Trim('"'))
        );
    }

    public async Task<PdfResponse> CreatePdfAsync(PdfInput input)
    {
        var templateParams = new Dictionary<string, object?>
        {
            ["voucher_number"] = input.NumeroDeFactura,
            ["sales_point"] = input.Request.PuntoDeVenta,
            ["issue_date"] = DateHelpers.FormatDateNumber(input.Fecha),
            ["cae_due_date"] = DateHelpers.FormatIsoDateForDisplay(input.CaeVencimiento),
            ["issuer_cuit"] = _options.Cuit,
            ["cae"] = input.Cae,
            ["issuer_business_name"] = "Empresa imaginaria S.A.",
            ["issuer_address"] = "Calle falsa 123",
            ["issuer_iva_condition"] = "Responsable inscripto",
            ["issuer_gross_income"] = _options.Cuit.ToString(CultureInfo.InvariantCulture),
            ["issuer_activity_start_date"] = DateHelpers.FormatDateNumber(input.Fecha),
            ["receiver_name"] = "Consumidor Final",
            ["receiver_address"] = "-",
            ["receiver_document_type"] = 99,
            ["receiver_document_number"] = input.Request.NumeroDeDocumento,
            ["receiver_iva_condition"] = input.Request.CondicionIvaReceptor.ToString(CultureInfo.InvariantCulture),
            ["sale_condition"] = "Contado",
            ["currency_id"] = "ARS",
            ["currency_rate"] = 1,
            ["concept"] = 1,
            ["items"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["code"] = "001",
                    ["description"] = "Servicio",
                    ["quantity"] = 1,
                    ["unit_price"] = input.ImporteTotal,
                    ["subtotal"] = input.ImporteTotal
                }
            },
            ["vat_amount"] = input.Request.ImporteIva,
            ["tributes_amount"] = 0,
            ["total_amount"] = input.ImporteTotal
        };

        if (input.Request.FechaServicioDesde is not null)
        {
            templateParams["billing_from"] = DateHelpers.FormatDateNumber(input.Request.FechaServicioDesde.Value);
        }

        if (input.Request.FechaServicioHasta is not null)
        {
            templateParams["billing_to"] = DateHelpers.FormatDateNumber(input.Request.FechaServicioHasta.Value);
        }

        if (input.Request.FechaVencimientoPago is not null)
        {
            templateParams["payment_due_date"] = DateHelpers.FormatDateNumber(input.Request.FechaVencimientoPago.Value);
        }

        var request = new
        {
            file_name = $"factura-b-{input.NumeroDeFactura.ToString().PadLeft(8, '0')}.pdf",
            template = new
            {
                name = "invoice-b",
                @params = templateParams
            }
        };

        var response = await PostJsonAsync("v1/pdfs", request);
        return JsonSerializer.Deserialize<PdfResponse>(response.GetRawText(), _jsonOptions)
            ?? throw new AfipSdkException("No se pudo leer la respuesta del PDF.");
    }

    private async Task<JsonElement> ExecuteElectronicBillingRequestAsync(string method, Dictionary<string, object?> parameters)
    {
        parameters["Auth"] = await GetServiceTaAsync("wsfe");

        var request = new
        {
            method,
            @params = parameters,
            environment = _options.Production ? "prod" : "dev",
            wsid = "wsfe",
            url = _options.Production
                ? "https://servicios1.afip.gov.ar/wsfev1/service.asmx"
                : "https://wswhomo.afip.gov.ar/wsfev1/service.asmx",
            wsdl = _options.Production ? "wsfe-production.wsdl" : "wsfe.wsdl",
            soap_v_1_2 = true
        };

        var response = await PostJsonAsync("v1/afip/requests", request);
        var result = response.GetProperty($"{method}Result");

        ThrowIfAfipError(method, result);
        return result;
    }

    private async Task<Dictionary<string, object?>> GetServiceTaAsync(string service)
    {
        var request = new Dictionary<string, object?>
        {
            ["environment"] = _options.Production ? "prod" : "dev",
            ["wsid"] = service,
            ["tax_id"] = _options.Cuit,
            ["force_create"] = false
        };

        if (!string.IsNullOrWhiteSpace(_options.Cert))
        {
            request["cert"] = _options.Cert;
        }

        if (!string.IsNullOrWhiteSpace(_options.Key))
        {
            request["key"] = _options.Key;
        }

        var response = await PostJsonAsync("v1/afip/auth", request);
        return new Dictionary<string, object?>
        {
            ["Token"] = response.GetProperty("token").GetString(),
            ["Sign"] = response.GetProperty("sign").GetString(),
            ["Cuit"] = _options.Cuit
        };
    }

    private async Task<JsonElement> PostJsonAsync(string url, object payload)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw AfipSdkException.FromHttpResponse(response, body);
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static void ThrowIfAfipError(string operation, JsonElement result)
    {
        if (operation == "FECAESolicitar" &&
            result.TryGetProperty("FeDetResp", out var feDetResp) &&
            feDetResp.TryGetProperty("FECAEDetResponse", out var detResponse))
        {
            if (detResponse.ValueKind == JsonValueKind.Array)
            {
                detResponse = detResponse[0];
            }

            if (detResponse.TryGetProperty("Observaciones", out var observations) &&
                detResponse.TryGetProperty("Resultado", out var resultado) &&
                resultado.GetString() != "A")
            {
                throw AfipSdkException.FromAfipError(observations.GetProperty("Obs"));
            }
        }

        if (result.TryGetProperty("Errors", out var errors))
        {
            throw AfipSdkException.FromAfipError(errors.GetProperty("Err"));
        }
    }
}

sealed class AfipSdkException : Exception
{
    public int? Code { get; }

    public AfipSdkException(string message, int? code = null) : base(message)
    {
        Code = code;
    }

    public static AfipSdkException FromHttpResponse(HttpResponseMessage response, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return new AfipSdkException(message.GetString() ?? response.ReasonPhrase ?? "Error en Afip SDK");
            }
        }
        catch (JsonException)
        {
        }

        return new AfipSdkException(response.ReasonPhrase ?? "Error en Afip SDK");
    }

    public static AfipSdkException FromAfipError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Array)
        {
            error = error[0];
        }

        var code = error.TryGetProperty("Code", out var codeElement) ? codeElement.GetInt32() : (int?)null;
        var message = error.TryGetProperty("Msg", out var messageElement) ? messageElement.GetString() : "Error de AFIP";
        return new AfipSdkException(code is null ? message ?? "Error de AFIP" : $"({code}) {message}", code);
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

sealed record VoucherInfo([property: JsonPropertyName("CbteFch")] int CbteFch);

sealed record CreateVoucherResponse(string Cae, string CaeFchVto);

sealed record PdfInput(
    BillRequest Request,
    int NumeroDeFactura,
    int Fecha,
    decimal ImporteTotal,
    string Cae,
    string CaeVencimiento
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

    public static string FormatAfipDateAsIso(string value)
    {
        return value.Length == 8 ? $"{value[0..4]}-{value[4..6]}-{value[6..8]}" : value;
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
