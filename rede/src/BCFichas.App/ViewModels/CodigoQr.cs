using Avalonia.Media.Imaging;
using QRCoder;

namespace BCFichas.App.ViewModels;

/// <summary>QR code para o celular abrir o painel (a câmera lê e já abre o endereço com o PIN).</summary>
internal static class CodigoQr
{
    public static Bitmap Imagem(string texto)
    {
        using var gerador = new QRCodeGenerator();
        using var dados = gerador.CreateQrCode(texto, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(dados).GetGraphic(8, drawQuietZones: true);
        using var fluxo = new MemoryStream(png);
        return new Bitmap(fluxo);
    }
}
