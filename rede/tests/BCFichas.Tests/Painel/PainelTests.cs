using System.Net;
using System.Net.Http.Json;
using BCFichas.Core;
using BCFichas.Core.Dados;
using BCFichas.Core.Vendas;
using BCFichas.Painel;
using BCFichas.Tests.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BCFichas.Tests.Painel;

/// <summary>
/// O painel da rede: lê o banco do caixa só para consulta, pede o PIN do evento e junta as vendas das outras
/// máquinas. Cada teste sobe servidores de verdade em portas livres do próprio computador.
/// </summary>
public class PainelTests : IAsyncLifetime
{
    private const string Pin = "246810";
    private readonly SistemaTemporario _a = new();
    private readonly SistemaTemporario _b = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly List<Servidor> _servidores = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var s in _servidores) await s.DisposeAsync();
        _http.Dispose();
        SqliteConnection.ClearAllPools();
        _a.Dispose();
        _b.Dispose();
    }

    private static void Ligar(Sistema s, int caixa, string maquinas = "")
    {
        var c = s.Config.Atual.Clonar();
        c.NomeEvento = "FESTA JUNINA";
        c.NumeroCaixa = caixa;
        c.PainelAtivo = true;
        c.PainelPin = Pin;
        c.PainelMaquinas = maquinas;
        s.Config.Salvar(c);
    }

    private static Pedido Vender(Sistema s, SessaoCaixa sessao, FormaPagamento forma, params (string Nome, int Qtd)[] itens)
    {
        var carrinho = new Carrinho();
        foreach (var (nome, qtd) in itens) carrinho.Adicionar(s.Catalogo.Produtos().First(p => p.Nome == nome), qtd);
        var pedido = s.Vendas.CriarPedido(sessao, carrinho.Linhas, forma, forma == FormaPagamento.Dinheiro ? 100000 : 0);
        return forma == FormaPagamento.Dinheiro ? pedido : s.Vendas.ConfirmarPagamento(pedido.Id, null);
    }

    private async Task<Servidor> Subir(SistemaTemporario t)
    {
        var s = await Servidor.Iniciar(t.Pasta, porta: 0, soLocal: true);
        _servidores.Add(s);
        return s;
    }

    private HttpRequestMessage Pedir(Servidor s, string caminho, string? pin = Pin, string? etag = null)
    {
        var pedido = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{s.Porta}{caminho}");
        if (pin is not null) pedido.Headers.Add(Servidor.CabecalhoPin, pin);
        if (etag is not null) pedido.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return pedido;
    }

    [Fact]
    public void Banco_so_de_leitura_le_e_nao_grava()
    {
        var banco = Banco.SomenteLeitura(Path.Combine(_a.Pasta, "bcfichas.db"));
        Assert.True(banco.Escalar<long>("SELECT COUNT(*) FROM produtos") > 0);
        Assert.Throws<SqliteException>(() => banco.Executar("DELETE FROM produtos"));
        Assert.True(banco.Escalar<long>("SELECT COUNT(*) FROM produtos") > 0);
        Assert.Throws<FileNotFoundException>(() => Banco.SomenteLeitura(Path.Combine(_a.Pasta, "nao-existe.db")));
    }

    [Fact]
    public async Task Mostra_as_vendas_desta_maquina_com_o_pin_e_nada_sem_ele()
    {
        Ligar(_a.Sistema, 1);
        var s = _a.Sistema;
        var sessao = s.Caixa.Abrir(1, null, 5000);
        Vender(s, sessao, FormaPagamento.Dinheiro, ("PASTEL", 2), ("CERVEJA", 1));   // R$ 28
        Vender(s, sessao, FormaPagamento.Pix, ("PASTEL", 1));                        // R$ 10
        s.Caixa.RegistrarMovimento(sessao, TipoMovimento.Sangria, 1000, "cofre");
        s.Caixa.RegistrarMovimento(sessao, TipoMovimento.Devolucao, 500, "");
        var servidor = await Subir(_a);

        // Sem PIN ou com PIN errado: nada
        using (var semPin = await _http.SendAsync(Pedir(servidor, "/api/v1/estado", pin: null)))
            Assert.Equal(HttpStatusCode.Unauthorized, semPin.StatusCode);
        using (var errado = await _http.SendAsync(Pedir(servidor, "/api/v1/estado", pin: "000000")))
            Assert.Equal(HttpStatusCode.Unauthorized, errado.StatusCode);

        // A identidade é pública (é como as máquinas se acham), sem números
        var info = await _http.GetFromJsonAsync<Info>($"http://127.0.0.1:{servidor.Porta}/api/v1/info", Json.Opcoes);
        Assert.Equal((1, "FESTA JUNINA", true), (info!.Caixa, info.NomeEvento, info.Ativo));

        using var resposta = await _http.SendAsync(Pedir(servidor, "/api/v1/estado"));
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var estado = (await resposta.Content.ReadFromJsonAsync<EstadoMaquina>(Json.Opcoes))!;
        var caixa = estado.CaixaAberto;
        Assert.Equal(3800, caixa.Vendido);
        Assert.Equal(500, caixa.Devolvido);
        Assert.Equal(3300, caixa.Liquido);
        Assert.Equal(2, caixa.Pedidos);
        Assert.Equal(1000, caixa.Sangrias);
        Assert.Equal(5000 + 2800 - 1000 - 500, caixa.DinheiroNoCaixa);
        Assert.Equal(1, caixa.CaixasAbertos);
        Assert.Equal(2800, caixa.Formas.Single(f => f.Chave == "dinheiro").Valor);
        Assert.Equal(1000, caixa.Formas.Single(f => f.Chave == "pix").Valor);
        Assert.Equal(("PASTEL", 3, 3000L), (caixa.Produtos[0].Nome, caixa.Produtos[0].Quantidade, caixa.Produtos[0].Valor));
        Assert.Equal(2, Assert.Single(caixa.PorHora).Pedidos);
        Assert.NotNull(caixa.UltimaVendaEm);
        // Um caixa só até agora: o evento todo é igual ao caixa aberto
        Assert.Equal((caixa.Vendido, caixa.Pedidos, caixa.Produtos.Count),
            (estado.TodoEvento.Vendido, estado.TodoEvento.Pedidos, estado.TodoEvento.Produtos.Count));
    }

    [Fact]
    public async Task Sem_venda_nova_responde_304_e_com_venda_nova_muda()
    {
        Ligar(_a.Sistema, 1);
        var sessao = _a.Sistema.Caixa.Abrir(1, null, 0);
        var servidor = await Subir(_a);

        using var primeira = await _http.SendAsync(Pedir(servidor, "/api/v1/estado"));
        var etag = primeira.Headers.ETag!.Tag;
        using (var igual = await _http.SendAsync(Pedir(servidor, "/api/v1/estado", etag: etag)))
            Assert.Equal(HttpStatusCode.NotModified, igual.StatusCode);

        Vender(_a.Sistema, sessao, FormaPagamento.Debito, ("PASTEL", 1));
        using var depois = await _http.SendAsync(Pedir(servidor, "/api/v1/estado", etag: etag));
        Assert.Equal(HttpStatusCode.OK, depois.StatusCode);
        Assert.NotEqual(etag, depois.Headers.ETag!.Tag);
        var estado = (await depois.Content.ReadFromJsonAsync<EstadoMaquina>(Json.Opcoes))!;
        Assert.Equal(1000, estado.CaixaAberto.Vendido);
    }

    [Fact]
    public async Task Cinco_pins_errados_bloqueiam_e_painel_desligado_nao_mostra_nada()
    {
        Ligar(_a.Sistema, 1);
        var servidor = await Subir(_a);
        for (var i = 0; i < 5; i++)
            using (await _http.SendAsync(Pedir(servidor, "/api/v1/estado", pin: "1111"))) { }
        using (var bloqueado = await _http.SendAsync(Pedir(servidor, "/api/v1/estado")))
            Assert.Equal(HttpStatusCode.TooManyRequests, bloqueado.StatusCode);

        var c = _a.Sistema.Config.Atual.Clonar();
        c.PainelAtivo = false;
        _a.Sistema.Config.Salvar(c);
        var outro = await Subir(_b);
        using var desligado = await _http.SendAsync(Pedir(outro, "/api/v1/estado"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, desligado.StatusCode);
    }

    [Fact]
    public async Task Junta_as_vendas_das_outras_maquinas_do_evento()
    {
        Ligar(_b.Sistema, 2);
        var b = _b.Sistema;
        var sessaoB = b.Caixa.Abrir(2, null, 2000);
        Vender(b, sessaoB, FormaPagamento.Credito, ("CERVEJA", 3)); // R$ 24
        var servidorB = await Subir(_b);

        Ligar(_a.Sistema, 1, maquinas: $"127.0.0.1:{servidorB.Porta}");
        var a = _a.Sistema;
        var sessaoA = a.Caixa.Abrir(1, null, 5000);
        Vender(a, sessaoA, FormaPagamento.Dinheiro, ("PASTEL", 2), ("CERVEJA", 1)); // R$ 28
        var servidorA = await Subir(_a);

        // A primeira pergunta acorda a consulta às outras máquinas; em instantes a máquina 2 aparece
        EstadoEvento? evento = null;
        for (var i = 0; i < 50 && evento?.Maquinas.Count != 2; i++)
        {
            using var r = await _http.SendAsync(Pedir(servidorA, "/api/v1/evento"));
            evento = await r.Content.ReadFromJsonAsync<EstadoEvento>(Json.Opcoes);
            await Task.Delay(100);
        }
        Assert.Equal(2, evento!.Maquinas.Count);
        Assert.Equal([1, 2], evento.Maquinas.Select(m => m.Estado.Caixa));
        Assert.True(evento.Maquinas.All(m => m.Online));
        Assert.Equal(2, evento.MaquinasOnline);
        Assert.Equal(2800 + 2400, evento.CaixaAberto.Vendido);
        Assert.Equal(4, evento.CaixaAberto.Produtos.Single(p => p.Nome == "CERVEJA").Quantidade);
        Assert.Equal(2400, evento.CaixaAberto.Formas.Single(f => f.Chave == "credito").Valor);
        Assert.Equal(5000 + 2800 + 2000, evento.CaixaAberto.DinheiroNoCaixa);

        // Venda nova na máquina 2: o painel da máquina 1 mostra em seguida
        using var antes = await _http.SendAsync(Pedir(servidorA, "/api/v1/evento"));
        var etag = antes.Headers.ETag!.Tag;
        Vender(b, sessaoB, FormaPagamento.Pix, ("PASTEL", 1));
        HttpResponseMessage? depois = null;
        for (var i = 0; i < 50; i++)
        {
            depois?.Dispose();
            depois = await _http.SendAsync(Pedir(servidorA, "/api/v1/evento", etag: etag));
            if (depois.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(100);
        }
        Assert.Equal(HttpStatusCode.OK, depois!.StatusCode);
        evento = await depois.Content.ReadFromJsonAsync<EstadoEvento>(Json.Opcoes);
        depois.Dispose();
        Assert.Equal(2800 + 2400 + 1000, evento!.CaixaAberto.Vendido);

        // A máquina 2 sai da rede: continua no painel com os últimos números, marcada sem conexão
        await servidorB.DisposeAsync();
        _servidores.Remove(servidorB);
        MaquinaNoPainel? dois = null;
        for (var i = 0; i < 60 && dois?.Online != false; i++)
        {
            using var r = await _http.SendAsync(Pedir(servidorA, "/api/v1/evento"));
            dois = (await r.Content.ReadFromJsonAsync<EstadoEvento>(Json.Opcoes))!.Maquinas.Single(m => m.Estado.Caixa == 2);
            await Task.Delay(250);
        }
        Assert.False(dois!.Online);
        Assert.Equal(3400, dois.Estado.CaixaAberto.Vendido);
    }

    [Fact]
    public async Task So_pergunta_as_outras_maquinas_quando_o_celular_pede()
    {
        // A máquina 2 de verdade, e na frente dela um "tablet" que só conta as perguntas e devolve os números dela
        Ligar(_b.Sistema, 2);
        Vender(_b.Sistema, _b.Sistema.Caixa.Abrir(2, null, 0), FormaPagamento.Pix, ("CERVEJA", 3)); // R$ 24
        var servidorB = await Subir(_b);
        var numerosB = await (await _http.SendAsync(Pedir(servidorB, "/api/v1/estado"))).Content.ReadAsByteArrayAsync();
        using var parar = new CancellationTokenSource();
        var contador = new int[1];
        var porta = Contador(numerosB, contador, parar.Token);

        Ligar(_a.Sistema, 1, maquinas: $"127.0.0.1:{porta}");
        var servidorA = await Subir(_a);

        // Ninguém abriu o painel: as máquinas não conversam
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(0, Volatile.Read(ref contador[0]));

        // O celular abriu o painel: a máquina 1 pergunta à 2 na hora e já responde com as duas
        using (var r = await _http.SendAsync(Pedir(servidorA, "/api/v1/evento")))
        {
            var evento = (await r.Content.ReadFromJsonAsync<EstadoEvento>(Json.Opcoes))!;
            Assert.Equal([1, 2], evento.Maquinas.Select(m => m.Estado.Caixa));
            Assert.Equal(2400, evento.CaixaAberto.Vendido);
        }
        Assert.Equal(1, Volatile.Read(ref contador[0]));

        // Dois celulares tocando em Atualizar juntos (ou um toque duplo): uma pergunta só
        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var r = await _http.SendAsync(Pedir(servidorA, "/api/v1/evento"));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }));
        Assert.Equal(1, Volatile.Read(ref contador[0]));

        // Painel aberto no celular, mas ninguém tocou em Atualizar: continuam quietas
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Volatile.Read(ref contador[0]));

        // Tocou em Atualizar: pergunta de novo
        using (var r = await _http.SendAsync(Pedir(servidorA, "/api/v1/evento")))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, Volatile.Read(ref contador[0]));
        parar.Cancel();
    }

    /// <summary>Um "tablet" que conta as perguntas que recebe e responde sempre os mesmos números.</summary>
    private static int Contador(byte[] numeros, int[] contador, CancellationToken parar)
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        parar.Register(l.Stop);
        _ = Task.Run(async () =>
        {
            while (!parar.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient c;
                try
                {
                    c = await l.AcceptTcpClientAsync(parar);
                }
                catch (Exception)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        try
                        {
                            var s = c.GetStream();
                            var buffer = new byte[8192];
                            var recebido = "";
                            int lidos;
                            while ((lidos = await s.ReadAsync(buffer, parar)) > 0)
                            {
                                recebido += System.Text.Encoding.ASCII.GetString(buffer, 0, lidos);
                                int fim;
                                while ((fim = recebido.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                                {
                                    var pedido = recebido[..fim];
                                    recebido = recebido[(fim + 4)..];
                                    Interlocked.Increment(ref contador[0]);
                                    var resposta = pedido.Contains("If-None-Match: \"x\"", StringComparison.OrdinalIgnoreCase)
                                        ? System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 304 Not Modified\r\nETag: \"x\"\r\n\r\n")
                                        : [.. System.Text.Encoding.ASCII.GetBytes(
                                            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {numeros.Length}\r\nETag: \"x\"\r\n\r\n"), .. numeros];
                                    await s.WriteAsync(resposta, parar);
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // fim do teste
                        }
                    }
                });
            }
        });
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Maquina_de_outro_evento_com_outro_pin_fica_de_fora()
    {
        Ligar(_b.Sistema, 2);
        var c = _b.Sistema.Config.Atual.Clonar();
        c.PainelPin = "999999";
        _b.Sistema.Config.Salvar(c);
        var servidorB = await Subir(_b);
        Ligar(_a.Sistema, 1, maquinas: $"127.0.0.1:{servidorB.Porta}");
        var servidorA = await Subir(_a);

        for (var i = 0; i < 10; i++)
        {
            using var r = await _http.SendAsync(Pedir(servidorA, "/api/v1/evento"));
            var evento = (await r.Content.ReadFromJsonAsync<EstadoEvento>(Json.Opcoes))!;
            Assert.Single(evento.Maquinas);
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Pagina_do_celular_vem_de_dentro_do_programa()
    {
        Ligar(_a.Sistema, 1);
        var servidor = await Subir(_a);
        var html = await _http.GetStringAsync($"http://127.0.0.1:{servidor.Porta}/");
        Assert.Contains("BC Fichas", html);
        Assert.DoesNotContain("https://", html); // nada da internet: o roteador do evento não tem
        using var icone = await _http.GetAsync($"http://127.0.0.1:{servidor.Porta}/icone.png");
        Assert.Equal("image/png", icone.Content.Headers.ContentType!.MediaType);
        using var nada = await _http.GetAsync($"http://127.0.0.1:{servidor.Porta}/../bcfichas.db");
        Assert.NotEqual(HttpStatusCode.OK, nada.StatusCode);
    }

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("200.150.10.1", false)]
    public void So_responde_a_enderecos_da_rede_local(string ip, bool local) =>
        Assert.Equal(local, Servidor.EnderecoLocal(IPAddress.Parse(ip)));

    [Fact]
    public void Lista_de_maquinas_digitada_aceita_espaco_virgula_e_porta()
    {
        Assert.Equal(["192.168.1.11:8765", "192.168.1.12:9000", "192.168.1.13:8765"],
            Rede.Enderecos("192.168.1.11, http://192.168.1.12:9000/\n192.168.1.13 192.168.1.11", 8765));
        Assert.Empty(Rede.Enderecos("  ", 8765));
    }
}
