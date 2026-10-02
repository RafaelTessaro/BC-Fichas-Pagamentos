namespace BCFichas.Core;

public enum TipoImpressora
{
    /// <summary>Impressora instalada no Windows (driver da Elgin), enviando ESC/POS cru.</summary>
    Windows = 0,
    /// <summary>Porta COM (USB-serial da Elgin i9).</summary>
    Serial = 1,
    /// <summary>Salva as fichas como imagem numa pasta (para testar sem impressora).</summary>
    Arquivo = 2,
}

public enum TipoMaquininha
{
    Simulador = 0,
}

public enum ModeloFicha
{
    /// <summary>Logo, nome do evento, produto grande, valor e dados do pedido.</summary>
    Completa = 1,
    /// <summary>Produto grande e uma linha de dados. Gasta menos papel.</summary>
    Compacta = 2,
    /// <summary>Produto e valor em destaque, com faixa preta no topo.</summary>
    Destaque = 3,
}

/// <summary>Telas que podem ser travadas com a senha master.</summary>
[Flags]
public enum TelaProtegida
{
    Nenhuma = 0,
    Produtos = 1,
    Reimpressao = 2,
    Sangria = 4,
    Relatorios = 8,
    FecharCaixa = 16,
    Configuracao = 32,
    SairDoPrograma = 64,
}

public sealed class Configuracao
{
    // Geral
    public string NomeEvento { get; set; } = "MINHA FESTA";
    public string Rodape { get; set; } = "Obrigado e volte sempre!";
    public int NumeroCaixa { get; set; } = 1;
    public bool DesligarAoFechar { get; set; }
    public bool TelaCheia { get; set; } = true;
    public bool TecladoNaTela { get; set; } = true;
    /// <summary>Tamanho da tela em %, para tablets pequenos ou com escala alta no Windows.</summary>
    public int Zoom { get; set; } = 100;

    // Grade de botões
    public int Colunas { get; set; } = 4;
    public int Linhas { get; set; } = 3;

    // Ficha
    public ModeloFicha Modelo { get; set; } = ModeloFicha.Completa;
    public string Fonte { get; set; } = "Impact";
    public bool CodigoDeBarras { get; set; }
    public bool MostrarValorNaFicha { get; set; } = true;
    public string? Logo { get; set; }

    // Impressora
    public TipoImpressora Impressora { get; set; } = TipoImpressora.Windows;
    public string NomeImpressora { get; set; } = "";
    public string PortaSerial { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public int LarguraPapelMm { get; set; } = 80;
    public bool CortarPapel { get; set; } = true;
    public string PastaArquivo { get; set; } = "";

    // Maquininha
    public TipoMaquininha Maquininha { get; set; } = TipoMaquininha.Simulador;
    public int SimuladorAprovarEmSegundos { get; set; } = 0;

    // Segurança
    public string SenhaMaster { get; set; } = "";
    public TelaProtegida TelasProtegidas { get; set; } =
        TelaProtegida.Configuracao | TelaProtegida.Produtos | TelaProtegida.Relatorios | TelaProtegida.SairDoPrograma;

    /// <summary>Largura de impressão em pontos (203 dpi): 80 mm = 576, 58 mm = 384.</summary>
    public int LarguraPontos => LarguraPapelMm <= 58 ? 384 : 576;

    public bool Protegida(TelaProtegida tela) =>
        !string.IsNullOrEmpty(SenhaMaster) && TelasProtegidas.HasFlag(tela);

    public Configuracao Clonar() => (Configuracao)MemberwiseClone();
}
