using BCFichas.Core;
using BCFichas.Core.Impressao;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>
/// Impressora pela porta COM que para no meio do pedido (o papel acabou na 4ª ficha): ao tentar de novo, só saem as
/// fichas que faltam. As que já saíram estão com o cliente e não podem sair duas vezes.
/// </summary>
public sealed class ImpressoraSerialTests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;
    public void Dispose() => _t.Dispose();

    /// <summary>Uma i9 de mentira: conta as fichas cortadas e para de receber depois de <see cref="Parar"/>.</summary>
    private sealed class ImpressoraDeMentira : Stream
    {
        public int? Parar { get; set; }
        public int Cortes { get; private set; }
        public List<byte> Recebido { get; } = [];
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Recebido.Count;
        public override long Position { get => Recebido.Count; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Parar is { } limite && Cortes >= limite) throw new TimeoutException("The write timed out.");
            for (var i = offset; i < offset + count; i++)
            {
                Recebido.Add(buffer[i]);
                var n = Recebido.Count;
                // GS V 66 0: corte parcial (uma ficha inteira saiu)
                if (n >= 4 && Recebido[n - 4] == 0x1D && Recebido[n - 3] == 0x56 && Recebido[n - 2] == 0x42 && Recebido[n - 1] == 0x00)
                    Cortes++;
            }
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private (Pedido Pedido, ImpressoraDeMentira Impressora) Preparar(string porta)
    {
        var config = S.Config.Atual.Clonar();
        config.Impressora = TipoImpressora.Serial;
        config.PortaSerial = porta;
        config.Corte = TipoCorte.Parcial;
        S.Config.Salvar(config);
        var impressora = new ImpressoraDeMentira();
        S.Impressao.AbrirPorta = (_, _) => impressora;

        var sessao = S.Caixa.Abrir(1, null, 0);
        var carrinho = new Carrinho();
        var produto = S.Catalogo.Produtos().First(p => !p.EhCombo);
        for (var i = 0; i < 10; i++) carrinho.Adicionar(produto);
        var pedido = S.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Dinheiro, 1_000_000);
        return (pedido, impressora);
    }

    [Fact]
    public void Papel_acaba_no_meio_e_tentar_de_novo_so_imprime_as_que_faltam()
    {
        var (pedido, impressora) = Preparar("COM91");
        Assert.Equal(10, pedido.QuantidadeFichas);

        // O papel acaba depois da 3ª ficha
        impressora.Parar = 3;
        var fichas = GeradorFichas.Gerar(pedido, S.Config.Atual);
        var primeira = S.Impressao.Fichas(fichas);
        Assert.False(primeira.Ok);
        Assert.Contains("saíram as fichas 1 a 3 de 10", primeira.Mensagem);
        Assert.Equal(3, impressora.Cortes);

        // Trocou o papel: tentar de novo (as mesmas fichas, como a tela faz) manda só as 7 que faltam
        impressora.Parar = null;
        var antes = impressora.Recebido.Count;
        var segunda = S.Impressao.Fichas(GeradorFichas.Gerar(S.Vendas.Pedido(pedido.Id)!, S.Config.Atual));
        Assert.True(segunda.Ok, segunda.Mensagem);
        Assert.Equal("7 fichas impressas", segunda.Mensagem);
        Assert.Equal(10, impressora.Cortes);
        // Antes da 4ª, zeros completam a imagem que tinha ficado pela metade dentro da impressora
        Assert.Equal(0, impressora.Recebido[antes]);

        // Um próximo envio do mesmo pedido (já saiu inteiro) imprime tudo de novo normalmente (ex.: reimpressão)
        var reimpressao = S.Impressao.Fichas(GeradorFichas.Gerar(S.Vendas.Pedido(pedido.Id)!, S.Config.Atual, reimpressao: true));
        Assert.True(reimpressao.Ok);
        Assert.Equal(20, impressora.Cortes);
    }

    [Fact]
    public void Impressora_que_nem_comeca_nao_marca_nenhuma_ficha_como_saida()
    {
        var (pedido, impressora) = Preparar("COM92");
        impressora.Parar = 0;
        var fichas = GeradorFichas.Gerar(pedido, S.Config.Atual);
        var erro = S.Impressao.Fichas(fichas);
        Assert.False(erro.Ok);
        Assert.StartsWith("Erro na impressora:", erro.Mensagem);

        impressora.Parar = null;
        var depois = S.Impressao.Fichas(fichas);
        Assert.True(depois.Ok);
        Assert.Equal("10 fichas impressas", depois.Mensagem);
        Assert.Equal(10, impressora.Cortes);
    }

    [Fact]
    public void Porta_que_nao_abre_avisa_sem_imprimir()
    {
        var (pedido, _) = Preparar("COM93");
        S.Impressao.AbrirPorta = (porta, _) => throw new ErroDeNegocio($"Não consegui abrir a porta {porta}: não existe");
        var erro = S.Impressao.Fichas(GeradorFichas.Gerar(pedido, S.Config.Atual));
        Assert.False(erro.Ok);
        Assert.Equal("Não consegui abrir a porta COM93: não existe", erro.Mensagem);
    }
}
