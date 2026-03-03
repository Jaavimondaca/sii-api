using Microsoft.AspNetCore.Mvc;
using Sii.RegistroCompraVenta.Services;
using System.Text.Json;

namespace Sii.RegistroCompraVenta.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BoletasController : ControllerBase
{
    private readonly BoletaHonorarioService _boletaService;

    public BoletasController(BoletaHonorarioService boletaService)
    {
        _boletaService = boletaService;
    }

    [HttpGet("recibidas")]
    public async Task<IActionResult> GetRecibidasMensual(
        [FromQuery] string rut,
        [FromQuery] int year,
        [FromQuery] int mes
    )
    {
        if (string.IsNullOrEmpty(rut))
            return BadRequest("El RUT es obligatorio.");
            
        if (year < 2000 || year > 2100)
            return BadRequest("El año es inválido.");
            
        if (mes < 1 || mes > 12)
            return BadRequest("El mes debe ser entre 1 y 12.");

        try
        {
            var periodo = new DateOnly(year, mes, 1);
            JsonElement resultado = await _boletaService.GetBoletasRecibidasMensual(rut, periodo);
            
            return Ok(resultado);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message, StackTrace = ex.StackTrace });
        }
    }
}
