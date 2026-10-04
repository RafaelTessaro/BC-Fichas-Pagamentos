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
    /// <summary>Maquininha de mentira, para treinar e testar.</summary>
    Simulador = 0,
    /// <summary>
    /// A maquininha é usada à parte (sem ligação com o programa): o operador cobra nela e confirma na tela.
    /// Cartão e PIX ficam registrados para o relatório.
    /// </summary>
    Separada = 1,
}

public enum ModeloFicha
{
    /// <summary>Logo, nome do evento, produto grande, valor e dados do pedido.</summary>
    Completa = 1,
    /// <summary>Produto grande e uma linha de dados. Gasta menos papel.</summary>
    Compacta = 2,
    /// <summary>Produto e valor em destaque, com faixa preta no topo.</summary>
    Destaque = 3,

    // Os quatro modelos do sistema antigo (fotos das fichas impressas).
    /// <summary>Modelo 1: evento, produto, valor numa tarja preta e logo ao lado dos dados.</summary>
    Classico1 = 11,
    /// <summary>Modelo 2: logo no topo ao lado do evento e da data, produto e valor embaixo.</summary>
    Classico2 = 12,
    /// <summary>Modelo 3: valor grande no topo com logo, produto e evento embaixo.</summary>
    Classico3 = 13,
    /// <summary>Modelo 4: evento e dados no topo, produto, logo e valor grande embaixo.</summary>
    Classico4 = 14,
}

/// <summary>O que a guilhotina faz depois de cada ficha.</summary>
public enum TipoCorte
{
    /// <summary>Corta deixando um ponto preso: a ficha não cai, o operador destaca.</summary>
    Parcial = 0,
    Total = 1,
    /// <summary>Sem guilhotina: só avança o papel para rasgar.</summary>
    Nenhum = 2,
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
    Devolucao = 128,
}

public sealed class Configuracao
{
    /// <summary>Linha da BC Fichas que sai no fim de toda ficha, sempre (não é o rodapé, que é do cliente).</summary>
    public const string MensagemFixa = "BC-FICHAS FONE: (19) 3023-9050";

    /// <summary>Telefone de suporte da BC Fichas (aparece na tela).</summary>
    public const string Suporte = "(19) 99821-6489";

    /// <summary>Versão das configurações gravadas; sobe quando um padrão novo precisa valer para quem já usa.</summary>
    public const int VersaoAtual = 1;

    /// <summary>Quanto o ajuste horizontal pode andar para cada lado, em pontos (8 pontos = 1 mm).</summary>
    public const int AjusteMaximo = 40;

    /// <summary>0 nas configurações gravadas antes da versão 3.2.</summary>
    public int VersaoConfig { get; set; }

    // Geral
    public string NomeEvento { get; set; } = "MINHA FESTA";
    /// <summary>Texto livre do cliente, acima da <see cref="MensagemFixa"/>. Vazio não imprime nada.</summary>
    public string Rodape { get; set; } = "";
    public int NumeroCaixa { get; set; } = 1;
    public bool DesligarAoFechar { get; set; }
    public bool TelaCheia { get; set; } = true;
    /// <summary>Enquanto o programa está aberto, o Windows não apaga a tela nem entra em suspensão.</summary>
    public bool ManterTelaLigada { get; set; } = true;
    /// <summary>Abre o BC Fichas sozinho quando o Windows liga (entrada "Executar" do usuário).</summary>
    public bool IniciarComWindows { get; set; } = true;
    public bool TecladoNaTela { get; set; } = true;
    /// <summary>Tamanho da tela em %, para tablets pequenos ou com escala alta no Windows.</summary>
    public int Zoom { get; set; } = 100;

    // Grade de botões
    public int Colunas { get; set; } = 4;
    public int Linhas { get; set; } = 3;
    /// <summary>Onde o botão "Imagem" do cadastro de produtos começa a procurar as fotos.</summary>
    public string PastaFotos { get; set; } = @"C:\Sistema_New\produtos";
    /// <summary>
    /// Onde o "Fazer backup" grava a programação e onde o "Restaurar" procura (pelo acesso remoto, é só pôr o
    /// arquivo .bcf nesta pasta da outra máquina).
    /// </summary>
    public string PastaBackup { get; set; } = @"C:\Sistema_New\backup";

    // Ficha
    public ModeloFicha Modelo { get; set; } = ModeloFicha.Classico2;
    public string Fonte { get; set; } = "Impact";
    public bool CodigoDeBarras { get; set; }
    public bool MostrarValorNaFicha { get; set; } = true;
    public string? Logo { get; set; }
    /// <summary>Borda em volta da ficha, como nas fichas antigas.</summary>
    public bool Moldura { get; set; } = true;

    // Impressora
    public TipoImpressora Impressora { get; set; } = TipoImpressora.Windows;
    public string NomeImpressora { get; set; } = "";
    public string PortaSerial { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public int LarguraPapelMm { get; set; } = 80;
    public TipoCorte Corte { get; set; } = TipoCorte.Parcial;
    public string PastaArquivo { get; set; } = "";
    /// <summary>
    /// Centraliza a impressão no papel: negativo puxa para a esquerda, positivo para a direita (em pontos,
    /// 8 pontos = 1 mm). A impressão fica um pouco mais estreita para caber.
    /// </summary>
    public int AjusteHorizontal { get; set; }

    // Maquininha
    public TipoMaquininha Maquininha { get; set; } = TipoMaquininha.Separada;
    public int SimuladorAprovarEmSegundos { get; set; } = 0;

    // Opções que a BC Fichas libera quando o cliente pede (aba Máquina, com a senha técnica)
    /// <summary>Devolução de fichas no Menu (só em dinheiro: o valor sai do caixa como uma sangria).</summary>
    public bool LiberarDevolucao { get; set; }
    /// <summary>Reimpressão de fichas (segunda via de um pedido) no Menu.</summary>
    public bool LiberarReimpressao { get; set; }

    // Painel no celular (rede do evento): cada caixa mostra as vendas de todas as máquinas, só para consulta
    /// <summary>Liga o painel nesta máquina (o programa BCFichasPainel.exe, ao lado do caixa).</summary>
    public bool PainelAtivo { get; set; }
    /// <summary>PIN do evento: o mesmo em todas as máquinas (vai junto no backup). Pedido no celular.</summary>
    public string PainelPin { get; set; } = "";
    /// <summary>
    /// Endereços das outras máquinas (ex.: "192.168.1.11 192.168.1.12"). Vazio: o painel procura sozinho na rede.
    /// </summary>
    public string PainelMaquinas { get; set; } = "";

    /// <summary>Porta do painel na rede (a mesma em todas as máquinas).</summary>
    public const int PortaPainel = 8765;

    /// <summary>PIN do painel: de 4 a 8 números.</summary>
    public static bool PinValido(string? pin) =>
        pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    // Segurança
    public string SenhaMaster { get; set; } = "";
    public TelaProtegida TelasProtegidas { get; set; } =
        TelaProtegida.Configuracao | TelaProtegida.Produtos | TelaProtegida.Relatorios | TelaProtegida.SairDoPrograma
        | TelaProtegida.Devolucao;

    /// <summary>Largura de impressão em pontos (203 dpi): 80 mm = 576, 58 mm = 384.</summary>
    public int LarguraPontos => LarguraPapelMm <= 58 ? 384 : 576;

    /// <summary>Largura do desenho depois do <see cref="AjusteHorizontal"/>.</summary>
    public int LarguraConteudo => LarguraPontos - 2 * Math.Abs(AjusteHorizontal);

    /// <summary>Tamanho das letras em relação ao papel de 80 mm.</summary>
    public float Escala => LarguraPontos / 576f;

    /// <summary>Onde o desenho começa na linha da impressora.</summary>
    public int InicioConteudo => AjusteHorizontal > 0 ? 2 * AjusteHorizontal : 0;

    public bool Protegida(TelaProtegida tela) =>
        !string.IsNullOrEmpty(SenhaMaster) && TelasProtegidas.HasFlag(tela);

    public Configuracao Clonar() => (Configuracao)MemberwiseClone();

    /// <summary>
    /// Cópia desta configuração com o que é de cada máquina vindo de <paramref name="maquina"/>: número do caixa,
    /// impressora (tipo, nome, porta, papel, corte, posição), maquininha, tela e Windows. Usado ao carregar a
    /// programação de outra máquina e ao zerar a programação (novo evento).
    /// </summary>
    public Configuracao ComDadosDaMaquina(Configuracao maquina)
    {
        var c = Clonar();
        c.NumeroCaixa = maquina.NumeroCaixa;
        c.TelaCheia = maquina.TelaCheia;
        c.TecladoNaTela = maquina.TecladoNaTela;
        c.Zoom = maquina.Zoom;
        c.ManterTelaLigada = maquina.ManterTelaLigada;
        c.IniciarComWindows = maquina.IniciarComWindows;
        c.DesligarAoFechar = maquina.DesligarAoFechar;
        c.PastaFotos = maquina.PastaFotos;
        c.PastaBackup = maquina.PastaBackup;
        c.Impressora = maquina.Impressora;
        c.NomeImpressora = maquina.NomeImpressora;
        c.PortaSerial = maquina.PortaSerial;
        c.BaudRate = maquina.BaudRate;
        c.LarguraPapelMm = maquina.LarguraPapelMm;
        c.Corte = maquina.Corte;
        c.PastaArquivo = maquina.PastaArquivo;
        c.AjusteHorizontal = maquina.AjusteHorizontal;
        // A maquininha é de cada máquina: um simulador que aprova sozinho (usado para treinar) não pode ir pelo
        // backup para as máquinas do evento e aprovar cartão sem cartão.
        c.Maquininha = maquina.Maquininha;
        c.SimuladorAprovarEmSegundos = maquina.SimuladorAprovarEmSegundos;
        c.VersaoConfig = maquina.VersaoConfig;
        return c;
    }

    /// <summary>Acerta configurações gravadas por versões antigas. Diz se mudou alguma coisa.</summary>
    public bool Atualizar()
    {
        if (VersaoConfig >= VersaoAtual) return false;
        if (VersaoConfig < 1)
        {
            // 3.2: a maquininha de cartão é usada separada do programa (só registra a forma de pagamento)
            // e a devolução de fichas (tira dinheiro do caixa) pede a senha master.
            if (Maquininha == TipoMaquininha.Simulador) Maquininha = TipoMaquininha.Separada;
            TelasProtegidas |= TelaProtegida.Devolucao;
        }
        VersaoConfig = VersaoAtual;
        return true;
    }

    /// <summary>
    /// Rodapé sem espaços nas pontas e sem repetir a <see cref="MensagemFixa"/>. Até a versão 3.0 o telefone da
    /// BC Fichas era o rodapé padrão; agora ele sai sozinho em toda ficha e, no rodapé, sairia duas vezes.
    /// </summary>
    public static string RodapeSemMensagemFixa(string? rodape)
    {
        var texto = (rodape ?? "").Trim();
        return string.Equals(texto, MensagemFixa, StringComparison.OrdinalIgnoreCase) ? "" : texto;
    }
}
