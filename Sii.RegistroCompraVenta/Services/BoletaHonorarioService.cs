using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sii.RegistroCompraVenta.Helper;

namespace Sii.RegistroCompraVenta.Services;

public class BoletaHonorarioService
{
    private readonly SiiAuthenticator _authenticator;
    private readonly IHttpClientFactory _httpClientFactory;
    private const string _clientName = "SII";

    // Mantenemos la URL Auth original que funciona en el SiiAuthenticator
    private const string UrlAuth = "https://herculesr.sii.cl/cgi_AUT2000/CAutInicio.cgi";

    public BoletaHonorarioService(
        SiiAuthenticator authenticator,
        IHttpClientFactory httpClientFactory
    )
    {
        _authenticator = authenticator;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<JsonElement> GetBoletasRecibidasMensual(string rutEmisor, DateOnly periodo)
    {
        try
        {
            // 1. Asegurar sesión activa
            string siiToken = await _authenticator.AutenticarAsync(UrlAuth);
            HttpClient client = _httpClientFactory.CreateClient(_clientName);
            
            (string rut, string dv) = ParseRut(rutEmisor);
            string mes = periodo.ToString("MM");
            string ano = periodo.ToString("yyyy");

            // 2. Armar el Form-Data clásico que capturamos de la pestaña Network
            var formContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("rut_arrastre", rut),
                new KeyValuePair<string, string>("dv_arrastre", dv),
                new KeyValuePair<string, string>("pagina_solicitada", "0"),
                new KeyValuePair<string, string>("cbmesinformemensual", mes),
                new KeyValuePair<string, string>("cbanoinformemensual", ano)
            });

            // Analizando la captura original y el payload, el endpoint exacto para boletas históricas
            // enviando datos form-urlencoded a una página clásica es en el subdominio LOA.
            string urlReal = "https://loa.sii.cl/cgi_IMT/TMBCOC_InformeMensualBheRec.cgi";
            
            // ATENCIÓN: El error "I082:host no definido" ocurre porque a estos CGI legacy
            // les falta el Referer, el Origin o un User-Agent de navegador normal.
            var requestMensaje = new HttpRequestMessage(HttpMethod.Post, urlReal)
            {
                Content = formContent
            };
            requestMensaje.Headers.Add("Referer", "https://loa.sii.cl/");
            requestMensaje.Headers.Add("Origin", "https://loa.sii.cl");
            requestMensaje.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            HttpResponseMessage response = await client.SendAsync(requestMensaje);
            
            response.EnsureSuccessStatusCode();

            // 4. Leer el contenido de la respuesta (que lamentablemente no es JSON, suele ser una tabla HTML escondida en un XML o similar)
            string rawHtml = await response.Content.ReadAsStringAsync();

            // 5. Parsear el HTML artesanalmente con Regex a JSON Array.
            // Dado que el SII devuelve el cuadro con clases tipo .table o <TR> y <TD> clásicos:
            var boletasStr = ExtractBoletasFromHtml(rawHtml, rut, dv, ano, mes);

            return JsonSerializer.Deserialize<JsonElement>(boletasStr);
        }
        catch (HttpRequestException ex)
        {
            throw new Exception($"Error de comunicación con SII Boletas: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new Exception($"Error en GetBoletasRecibidasMensual: {ex.Message}", ex);
        }
    }

    private static (string Rut, string Dv) ParseRut(string rutEmisor)
    {
        string[] parts = rutEmisor.Split('-');
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Intenta extraer los Tds de la tabla y armar una estructura JSON compatible
    /// con lo que consume Latenode y Google Sheets.
    /// </summary>
    private string ExtractBoletasFromHtml(string html, string rut, string dv, string ano, string mes)
    {
        /*
          El portal de honorarios generalmente devuelve data dentro de un tag de error, 
          o si es exitoso, devuelve strings escapados de HTML con las tablas.
          
          Como el SII en "services/data/facadeService" de honorarios SÍ ESTÁ transicionando 
          a un JSON mal formateado (a veces envían un JSON con un atributo 'data' 
          que tiene strings grandotes HTML adentro, o arrays de objetos crudos), intentamos
          primero parsear como JSON puro por si tuvimos la suerte de que el Facade devolvió JSON Array.
        */
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(html);
            // Si funciona, devolvemos el rawHtml tal cual. Significa que el facade SÍ era un servicio REST jsonificado.
            return html;
        }
        catch 
        {
            // Omitimos, era HTML puro o String corrupto.
        }

        // Si llegó acá, es HTML puro de tabla en el Body. Scrapear fila por fila (TR, TD).
        // Hacemos una validación regex en bruto (esto es muy frágil pero funciona al no tener HtmlAgilityPack instalado en local)
        
        var facturasList = new List<object>();

        // Busca todos los <tr>...</tr>
        var trMatches = Regex.Matches(html, @"(?i)<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline);
        
        foreach (Match tr in trMatches)
        {
            string trContent = tr.Groups[1].Value;
            var tdMatches = Regex.Matches(trContent, @"(?i)<td[^>]*>(.*?)</td>", RegexOptions.Singleline);
            
            if (tdMatches.Count >= 6) // Usualmente son Rut, Nombre, Nro, Fecha, Bruto, Retenido, Liquido
            {
                // Limpiar tags HTML interiores (ej: <span>, <a>)
                string CleanTd(int index) => Regex.Replace(tdMatches[index].Groups[1].Value, "<.*?>", "").Trim();
                
                string rutBoleta = CleanTd(0);
                string rznSocial = CleanTd(1);
                string numBoleta = CleanTd(2);
                string fecha = CleanTd(3);
                string brutoStr = CleanTd(4).Replace("$", "").Replace(".", "").Trim();
                string retenidoStr = CleanTd(5).Replace("$", "").Replace(".", "").Trim();
                string liquidoStr = tdMatches.Count >= 7 ? CleanTd(6).Replace("$", "").Replace(".", "").Trim() : brutoStr;
                
                // Si el RUT tiene guión, probablemente es una boleta válida (ignorar headers de la tabla)
                if (rutBoleta.Contains("-")) 
                {
                    facturasList.Add(new {
                        detRutDoc = rutBoleta.Split('-')[0],
                        detDvDoc = rutBoleta.Split('-')[1],
                        detRznSoc = rznSocial,
                        detNroDoc = numBoleta,
                        detFchDoc = fecha,
                        detMntNeto = decimal.TryParse(brutoStr, out decimal bruto) ? bruto : 0,
                        detMntRetenido = decimal.TryParse(retenidoStr, out decimal ret) ? ret : 0,
                        detMntTotal = decimal.TryParse(liquidoStr, out decimal liq) ? liq : 0,
                        detTipoDoc = "110", // Código estándar genérico "Boleta de Honorarios"
                        detPcarga = ano + mes // ej: 202601
                    });
                }
            }
        }

        // Devolver un objeto similar al que bota el SII de Compras/Ventas: {"metaData": {}, "data": [...]}
        // Para modo debug: Si la lista está vacía, retornar el HTML en metadata para ver qué llegó
        var wrapper = new {
            metaData = facturasList.Count == 0 ? new { rawHtmlDebug = html } : new object(),
            data = facturasList
        };

        return JsonSerializer.Serialize(wrapper);
    }
}
