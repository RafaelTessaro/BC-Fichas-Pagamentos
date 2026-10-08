using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>
/// Impressora pela porta COM que para no meio do pedido (o papel acabou, o cabo soltou): ao imprimir de novo, mesmo
/// depois de fechar o programa, só saem as fichas que faltam. As que já saíram estão com o cliente e não podem sair
/// duas vezes. E a imagem que ficou pela metade dentro da impressora é completada antes do próximo trabalho.
/// </summary>
public sealed class ImpressoraSerialTests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;
    public void Dispose() => _t.Dispose();

    /// <summary>Uma i9 de mentira: conta as fichas cortadas e para de receber depois de <see cref="Parar"/>.</summary>
    private sealed class ImpressoraDeMentira : Stream
    {
        private bool _jaEntrouMetade;
        public int? Parar { get; set; }
        /// <summary>Ao parar, o primeiro envio volta sem erro com só metade dentro (como a porta COM do Windows).</summary>
        public bool MetadeAntes { get; set; }
        /// <summary>O erro ao parar (tempo esgotado ou cabo arrancado).</summary>
        public Func<Exception> Erro { get; set; } = () => new TimeoutException("The write timed out.");
        public int Cortes { get; private set; }
        public List<int> FimDosCortes { get; } = [];
        public List<byte> Recebido { get; } = [];
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Recebido.Count;
        public override long Position { get => Recebido.Count; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Parar is { } limite && Cortes >= limite)
            {
                if (!MetadeAntes || _jaEntrouMetade) throw Erro();
                _jaEntrouMetade = true;
                count /= 2;
            }
            for (var i = offset; i < offset + count; i++)
            {
                Recebido.Add(buffer[i]);
                var n = Recebido.Count;
                // GS V 66 0: corte parcial (uma ficha inteira saiu)
                if (n >= 4 && Recebido[n - 4] == 0x1D && Recebido[n - 3] == 0x56 && Recebido[n - 2] == 0x42 && Recebido[n - 1] == 0x00)
                {
                    Cortes++;
                    FimDosCortes.Add(n);
                }
            }
        }

        public void Destravar()
        {
            Parar = null;
            _jaEntrouMetade = false;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private ImpressoraDeMentira Configurar(string porta)
    {
        var config = S.Config.Atual.Clonar();
        config.Impressora = TipoImpressora.Serial;
        config.PortaSerial = porta;
        config.Corte = TipoCorte.Parcial;
        S.Config.Salvar(config);
        var impressora = new ImpressoraDeMentira();
        S.Impressao.AbrirPorta = (_, _) => impressora;
        return impressora;
    }

    private Pedido Vender(SessaoCaixa sessao, int fichas)
    {
        var carrinho = new Carrinho();
        var produto = S.Catalogo.Produtos().First(p => !p.EhCombo && p.FichasPorUnidade <= 1);
        for (var i = 0; i < fichas; i++) carrinho.Adicionar(produto);
        return S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 1_000_000);
    }

    [Fact]
    public void Papel_acaba_no_meio_e_mesmo_depois_de_fechar_o_programa_so_saem_as_que_faltam()
    {
        var impressora = Configurar("COM91");
        var pedido = Vender(S.Caixa.Abrir(1, null, 0), 10);
        Assert.Equal(10, pedido.QuantidadeFichas);

        // O papel acaba depois da 3ª ficha
        impressora.Parar = 3;
        var primeira = S.ImprimirFichas(pedido, GeradorFichas.Gerar(pedido, S.Config.Atual));
        Assert.False(primeira.Ok);
        Assert.Contains("saíram as fichas 1 a 3 de 10", primeira.Mensagem);
        Assert.Equal(3, impressora.Cortes);
        Assert.Equal(3, S.Vendas.Pedido(pedido.Id)!.FichasSaidas); // gravado no pedido

        // Trocou o papel e até fechou o programa: o pedido lido do banco manda só as 7 que faltam
        impressora.Destravar();
        var antes = impressora.Recebido.Count;
        var doBanco = S.Vendas.Pedido(pedido.Id)!;
        var segunda = S.ImprimirFichas(doBanco, GeradorFichas.Gerar(doBanco, S.Config.Atual));
        Assert.True(segunda.Ok, segunda.Mensagem);
        Assert.Equal("7 fichas impressas", segunda.Mensagem);
        Assert.Equal(10, impressora.Cortes);
        // Antes da 4ª, zeros completam a imagem que tinha ficado pela metade dentro da impressora
        Assert.Equal(0, impressora.Recebido[antes]);
        S.Vendas.RegistrarImpressao(pedido.Id);
        Assert.Equal(0, S.Vendas.Pedido(pedido.Id)!.FichasSaidas);

        // A reimpressão sai inteira (marcada REIMPRESSÃO)
        var reimpressao = S.ImprimirFichas(S.Vendas.Pedido(pedido.Id)!,
            GeradorFichas.Gerar(S.Vendas.Pedido(pedido.Id)!, S.Config.Atual, reimpressao: true));
        Assert.True(reimpressao.Ok);
        Assert.Equal(20, impressora.Cortes);
    }

    [Fact]
    public void Pedido_do_modo_teste_que_parou_no_meio_nao_tira_fichas_do_pedido_de_verdade_com_o_mesmo_numero()
    {
        var impressora = Configurar("COM92");
        var teste = Vender(S.Caixa.Abrir(1, null, 0, teste: true), 5);
        impressora.Parar = 2;
        Assert.False(S.ImprimirFichas(teste, GeradorFichas.Gerar(teste, S.Config.Atual)).Ok);
        S.Caixa.ApagarTestes();

        // O primeiro pedido de verdade tem o mesmo número (1) e o mesmo caixa: sai inteiro
        impressora.Destravar();
        var real = Vender(S.Caixa.Abrir(1, null, 0), 2);
        Assert.Equal(teste.Numero, real.Numero);
        var resultado = S.ImprimirFichas(real, GeradorFichas.Gerar(real, S.Config.Atual));
        Assert.True(resultado.Ok);
        Assert.Equal("2 fichas impressas", resultado.Mensagem);
    }

    [Fact]
    public void Cabo_arrancado_no_meio_tambem_conta_as_fichas_que_ja_sairam()
    {
        var impressora = Configurar("COM93");
        var pedido = Vender(S.Caixa.Abrir(1, null, 0), 6);
        impressora.Parar = 2;
        impressora.Erro = () => new UnauthorizedAccessException("Access to the port 'COM93' is denied.");
        var erro = S.ImprimirFichas(pedido, GeradorFichas.Gerar(pedido, S.Config.Atual));
        Assert.False(erro.Ok);
        Assert.Contains("saíram as fichas 1 a 2 de 6", erro.Mensagem);
        Assert.Equal(2, S.Vendas.Pedido(pedido.Id)!.FichasSaidas);
    }

    [Fact]
    public void Envio_que_entrou_so_pela_metade_e_completado_com_zeros_suficientes()
    {
        var impressora = Configurar("COM94");
        var pedido = Vender(S.Caixa.Abrir(1, null, 0), 6);
        impressora.Parar = 3;
        impressora.MetadeAntes = true; // a porta engoliu só metade de um pedaço e voltou sem erro
        Assert.False(S.ImprimirFichas(pedido, GeradorFichas.Gerar(pedido, S.Config.Atual)).Ok);
        var tamanhoDaFicha = impressora.FimDosCortes[2] - impressora.FimDosCortes[1];
        var recebidoDaQuarta = impressora.Recebido.Count - impressora.FimDosCortes[2];
        var faltavam = tamanhoDaFicha - recebidoDaQuarta;
        Assert.True(faltavam > 0);

        impressora.Destravar();
        var antes = impressora.Recebido.Count;
        Assert.True(S.ImprimirFichas(S.Vendas.Pedido(pedido.Id)!,
            GeradorFichas.Gerar(S.Vendas.Pedido(pedido.Id)!, S.Config.Atual)).Ok);
        var zeros = impressora.Recebido.Skip(antes).TakeWhile(b => b == 0).Count();
        Assert.True(zeros >= faltavam, $"vieram {zeros} zeros, faltavam {faltavam} bytes da 4ª ficha");
    }

    [Fact]
    public void Impressora_que_nem_comeca_nao_marca_nenhuma_ficha_como_saida()
    {
        var impressora = Configurar("COM95");
        var pedido = Vender(S.Caixa.Abrir(1, null, 0), 10);
        impressora.Parar = 0;
        var erro = S.ImprimirFichas(pedido, GeradorFichas.Gerar(pedido, S.Config.Atual));
        Assert.False(erro.Ok);
        Assert.StartsWith("Erro na impressora:", erro.Mensagem);
        Assert.Equal(0, S.Vendas.Pedido(pedido.Id)!.FichasSaidas);

        impressora.Destravar();
        var depois = S.ImprimirFichas(pedido, GeradorFichas.Gerar(pedido, S.Config.Atual));
        Assert.True(depois.Ok);
        Assert.Equal("10 fichas impressas", depois.Mensagem);
        Assert.Equal(10, impressora.Cortes);
    }

    [Fact]
    public void Porta_que_nao_abre_avisa_sem_imprimir()
    {
        Configurar("COM96");
        var pedido = Vender(S.Caixa.Abrir(1, null, 0), 2);
        S.Impressao.AbrirPorta = (porta, _) => throw new ErroDeNegocio($"Não consegui abrir a porta {porta}: não existe");
        var erro = S.ImprimirFichas(pedido, GeradorFichas.Gerar(pedido, S.Config.Atual));
        Assert.False(erro.Ok);
        Assert.Equal("Não consegui abrir a porta COM96: não existe", erro.Mensagem);
    }
}
