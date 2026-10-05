using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using BCFichas.Core;
using BCFichas.Core.Vendas;
using BCFichas.Painel;
using BCFichas.Tests.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BCFichas.Tests.Painel;

/// <summary>
/// Auditoria do painel da rede: os números do celular batem com os relatórios do caixa e a rede do evento
/// (máquinas que mudam de endereço, reabrem, saem da lista, têm outro PIN ou respondem lixo) não faz o painel
/// contar errado nem parar de atender.
/// </summary>
public class AuditoriaDoPainelTests : IAsyncLifetime
{
    private const string Pin = "246810";
    private readonly List<SistemaTemporario> _maquinas = [];
    private readonly List<Servidor> _servidores = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var s in _servidores) await s.DisposeAsync();
        _http.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var m in _maquinas) m.Dispose();
    }

    private SistemaTemporario Maquina(int caixa, string outras = "", string pin = Pin)
    {
        var t = new SistemaTemporario();
        _maquinas.Add(t);
        Ligar(t.Sistema, caixa, outras, pin);
        return t;
    }

    private static void Ligar(Sistema s, int caixa, string outras = "", string pin = Pin)
    {
        var c = s.Config.Atual.Clonar();
        c.NomeEvento = "FESTA JUNINA";
        c.NumeroCaixa = caixa;
        c.PainelAtivo = true;
        c.PainelPin = pin;
        c.PainelMaquinas = outras;
        s.Config.Salvar(c);
    }

    private static Pedido Vender(Sistema s, SessaoCaixa sessao, FormaPagamento forma, params (string Nome, int Qtd)[] itens)
    {
        var carrinho = new Carrinho();
        foreach (var (nome, qtd) in itens) carrinho.Adicionar(s.Catalogo.Produtos().First(p => p.Nome == nome), qtd);
        var pedido = s.Vendas.CriarPedido(sessao, carrinho.Linhas, forma, forma == FormaPagamento.Dinheiro ? 100000 : 0);
        return forma == FormaPagamento.Dinheiro ? pedido : s.Vendas.ConfirmarPagamento(pedido.Id, null);
    }

    private async Task<Servidor> Subir(SistemaTemporario t, int porta = 0)
    {
        var s = await Servidor.Iniciar(t.Pasta, porta, intervaloRede: TimeSpan.FromMilliseconds(200), soLocal: true);
        _servidores.Add(s);
        return s;
    }

    private async Task Derrubar(Servidor s)
    {
        await s.DisposeAsync();
        _servidores.Remove(s);
    }

    private static int PortaLivre()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var porta = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return porta;
    }

    private async Task<HttpResponseMessage> Pedir(Servidor s, string caminho, string? pin = Pin)
    {
        var pedido = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{s.Porta}{caminho}");
        if (pin is not null) pedido.Headers.Add(Servidor.CabecalhoPin, pin);
        return await _http.SendAsync(pedido);
    }

    private async Task<EstadoEvento> Evento(Servidor s)
    {
        using var r = await Pedir(s, "/api/v1/evento");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<EstadoEvento>(Json.Opcoes))!;
    }

    /// <summary>Pergunta até a condição valer (a rede do painel consulta as outras a cada 200 ms nos testes).</summary>
    private async Task<EstadoEvento> EsperarEvento(Servidor s, Func<EstadoEvento, bool> condicao, int segundos = 10)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        EstadoEvento evento;
        do
        {
            evento = await Evento(s);
            if (condicao(evento)) return evento;
            await Task.Delay(100);
        } while (DateTime.UtcNow < limite);
        return evento;
    }

    // ---------------------------------------------------------------------------------------------------------
    // A mesma máquina nunca entra duas vezes na soma
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mesma_maquina_achada_em_dois_enderecos_conta_uma_vez_so()
    {
        // Ex.: o tablet com Wi-Fi e cabo na mesma rede, ou o nome e o IP digitados na lista
        var b = Maquina(2);
        var sessaoB = b.Sistema.Caixa.Abrir(2, null, 0);
        Vender(b.Sistema, sessaoB, FormaPagamento.Pix, ("PASTEL", 3)); // R$ 30
        var servidorB = await Subir(b);

        var a = Maquina(1, $"127.0.0.1:{servidorB.Porta} localhost:{servidorB.Porta}");
        var sessaoA = a.Sistema.Caixa.Abrir(1, null, 0);
        Vender(a.Sistema, sessaoA, FormaPagamento.Dinheiro, ("CERVEJA", 1)); // R$ 8
        var servidorA = await Subir(a);

        // Dá tempo de os dois endereços responderem
        await EsperarEvento(servidorA, e => e.Maquinas.Count >= 2);
        await Task.Delay(1500);
        var evento = await Evento(servidorA);

        Assert.Equal([1, 2], evento.Maquinas.Select(m => m.Estado.Caixa));
        Assert.Equal(3000 + 800, evento.CaixaAberto.Vendido);
        Assert.Equal(3000 + 800, evento.TodoEvento.Vendido);
        Assert.Equal(2, evento.MaquinasTotal);
    }

    [Fact]
    public async Task Maquina_que_reabriu_em_outro_endereco_nao_conta_em_dobro()
    {
        // O caixa 2 fechou e abriu de novo (painel novo) e o roteador deu outro IP para ele: o endereço antigo fica
        // sem resposta, com os últimos números, e o novo responde com os mesmos números e mais.
        var b = Maquina(2);
        var sessaoB = b.Sistema.Caixa.Abrir(2, null, 0);
        Vender(b.Sistema, sessaoB, FormaPagamento.Credito, ("PASTEL", 2)); // R$ 20
        var portaAntiga = PortaLivre();
        var portaNova = PortaLivre();
        // O endereço antigo vem antes na ordem alfabética: no empate, ele não pode ganhar do novo
        if (string.CompareOrdinal($"{portaAntiga}", $"{portaNova}") > 0) (portaAntiga, portaNova) = (portaNova, portaAntiga);
        var servidorB = await Subir(b, portaAntiga);

        var a = Maquina(1, $"127.0.0.1:{portaAntiga} 127.0.0.1:{portaNova}");
        var servidorA = await Subir(a);
        var evento = await EsperarEvento(servidorA, e => e.Maquinas.Count == 2);
        Assert.Equal(2000, evento.CaixaAberto.Vendido);

        await Derrubar(servidorB);
        Vender(b.Sistema, sessaoB, FormaPagamento.Pix, ("CERVEJA", 1)); // + R$ 8 enquanto estava fora
        await Subir(b, portaNova);

        evento = await EsperarEvento(servidorA, e => e.CaixaAberto.Vendido >= 2800 && e.Maquinas.All(m => m.Online));
        await Task.Delay(1000);
        evento = await Evento(servidorA);
        Assert.Equal(2800, evento.CaixaAberto.Vendido);
        Assert.Equal(2800, evento.TodoEvento.Vendido);
        Assert.Equal(2, evento.MaquinasTotal);
        Assert.All(evento.Maquinas, m => Assert.True(m.Online));
    }

    [Fact]
    public async Task Duas_maquinas_com_o_mesmo_numero_de_caixa_entram_as_duas()
    {
        var b = Maquina(2);
        Vender(b.Sistema, b.Sistema.Caixa.Abrir(2, null, 0), FormaPagamento.Pix, ("PASTEL", 1));
        var c = Maquina(2);
        Vender(c.Sistema, c.Sistema.Caixa.Abrir(2, null, 0), FormaPagamento.Pix, ("PASTEL", 2));
        var servidorB = await Subir(b);
        var servidorC = await Subir(c);
        var a = Maquina(1, $"127.0.0.1:{servidorB.Porta} 127.0.0.1:{servidorC.Porta}");
        var servidorA = await Subir(a);

        var evento = await EsperarEvento(servidorA, e => e.Maquinas.Count == 3);
        Assert.Equal([1, 2, 2], evento.Maquinas.Select(m => m.Estado.Caixa));
        Assert.Equal(3000, evento.CaixaAberto.Vendido);
    }

    [Fact]
    public async Task Maquina_tirada_da_lista_sai_do_painel()
    {
        var b = Maquina(2);
        Vender(b.Sistema, b.Sistema.Caixa.Abrir(2, null, 0), FormaPagamento.Pix, ("PASTEL", 1));
        var c = Maquina(3);
        Vender(c.Sistema, c.Sistema.Caixa.Abrir(3, null, 0), FormaPagamento.Pix, ("CERVEJA", 1));
        var servidorB = await Subir(b);
        var servidorC = await Subir(c);
        var a = Maquina(1, $"127.0.0.1:{servidorB.Porta} 127.0.0.1:{servidorC.Porta}");
        var servidorA = await Subir(a);
        Assert.Equal(3, (await EsperarEvento(servidorA, e => e.Maquinas.Count == 3)).Maquinas.Count);

        // A máquina 2 era de outro evento (digitada por engano): sai da lista nas configurações
        Ligar(a.Sistema, 1, $"127.0.0.1:{servidorC.Porta}");
        var evento = await EsperarEvento(servidorA, e => e.Maquinas.Count == 2);
        Assert.Equal([1, 3], evento.Maquinas.Select(m => m.Estado.Caixa));
        Assert.Equal(800, evento.CaixaAberto.Vendido);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Rede do evento: PIN diferente, lixo, muitos celulares
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Maquina_com_outro_pin_na_lista_nao_e_bloqueada_de_tanto_perguntar()
    {
        // A máquina 2 ficou com outro PIN (backup antigo). O painel da 1 não pode ficar perguntando com o PIN
        // errado a cada 3 s: em 5 tentativas a 2 bloqueia o tablet 1 por 5 minutos, e mesmo depois de o PIN ser
        // corrigido a máquina 2 continua fora do painel.
        var b = Maquina(2, pin: "999999");
        Vender(b.Sistema, b.Sistema.Caixa.Abrir(2, null, 0), FormaPagamento.Pix, ("PASTEL", 1));
        var servidorB = await Subir(b);
        var a = Maquina(1, $"127.0.0.1:{servidorB.Porta}");
        var servidorA = await Subir(a);
        for (var i = 0; i < 15; i++)
        {
            Assert.Single((await Evento(servidorA)).Maquinas);
            await Task.Delay(200);
        }

        // Nos testes o "celular" e o tablet 1 têm o mesmo endereço (127.0.0.1): o bloqueio aparece aqui
        using var r = await Pedir(servidorB, "/api/v1/estado", "999999");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Outra_coisa_respondendo_na_porta_com_resposta_enorme_fica_de_fora()
    {
        // Um aparelho qualquer na porta 8765 que responde 200 com um JSON enorme: o painel não pode guardar isso
        // nem mandar para os celulares a cada 3 s.
        using var parar = new CancellationTokenSource();
        var corpo = Encoding.UTF8.GetBytes(
            "{\"instancia\":\"" + new string('x', 6_000_000) + "\",\"caixa\":7,\"nomeEvento\":\"\",\"versao\":\"\"," +
            "\"agora\":\"2026-01-01T00:00:00\",\"caixaAberto\":{\"vendido\":100},\"todoEvento\":{\"vendido\":100}}");
        var porta = Falso(parar.Token, corpo);
        var a = Maquina(1, $"127.0.0.1:{porta}");
        var servidorA = await Subir(a);

        for (var i = 0; i < 10; i++)
        {
            using var r = await Pedir(servidorA, "/api/v1/evento");
            var bytes = await r.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length < 100_000, $"evento com {bytes.Length} bytes");
            await Task.Delay(200);
        }
        parar.Cancel();
    }

    /// <summary>Um servidor qualquer que responde sempre o mesmo corpo com 200.</summary>
    private static int Falso(CancellationToken parar, byte[] corpo)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        parar.Register(l.Stop);
        _ = Task.Run(async () =>
        {
            while (!parar.IsCancellationRequested)
            {
                TcpClient c;
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
                            while (await s.ReadAsync(buffer, parar) > 0)
                            {
                                var cab = Encoding.ASCII.GetBytes(
                                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {corpo.Length}\r\nETag: \"x\"\r\n\r\n");
                                await s.WriteAsync(cab, parar);
                                await s.WriteAsync(corpo, parar);
                            }
                        }
                        catch (Exception)
                        {
                            // o painel desistiu no meio
                        }
                    }
                });
            }
        });
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Maquina_lenta_nao_atrasa_o_celular_nem_derruba_as_outras()
    {
        // Um tablet travado aceita a conexão e nunca responde: o celular continua recebendo na hora e a máquina 2
        // continua "respondendo" (a consulta das outras não espera a lenta para sempre).
        var lenta = new TcpListener(IPAddress.Loopback, 0);
        lenta.Start();
        var presas = new List<TcpClient>();
        using var parar = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true) presas.Add(await lenta.AcceptTcpClientAsync(parar.Token));
            }
            catch (Exception)
            {
                // fim do teste
            }
        });
        try
        {
            var b = Maquina(2);
            Vender(b.Sistema, b.Sistema.Caixa.Abrir(2, null, 0), FormaPagamento.Pix, ("PASTEL", 1));
            var servidorB = await Subir(b);
            var a = Maquina(1, $"127.0.0.1:{((IPEndPoint)lenta.LocalEndpoint).Port} 127.0.0.1:{servidorB.Porta}");
            var servidorA = await Subir(a);

            await EsperarEvento(servidorA, e => e.Maquinas.Count == 2);
            var relogio = Stopwatch.StartNew();
            while (relogio.Elapsed < TimeSpan.FromSeconds(6))
            {
                var pergunta = Stopwatch.StartNew();
                var evento = await Evento(servidorA);
                Assert.True(pergunta.ElapsedMilliseconds < 1000, $"o celular esperou {pergunta.ElapsedMilliseconds} ms");
                Assert.True(evento.Maquinas.All(m => m.Online));
                Assert.Equal(1000, evento.CaixaAberto.Vendido);
                await Task.Delay(300);
            }
        }
        finally
        {
            parar.Cancel();
            lenta.Stop();
            foreach (var c in presas.ToList()) c.Dispose();
        }
    }

    [Fact]
    public async Task Endereco_ou_cabecalho_gigante_e_recusado_e_o_painel_continua_atendendo()
    {
        var a = Maquina(1);
        var servidor = await Subir(a);
        using (var pedido = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{servidor.Porta}/api/v1/evento?x={new string('a', 20_000)}"))
        using (var r = await _http.SendAsync(pedido))
            Assert.Equal(HttpStatusCode.RequestUriTooLong, r.StatusCode);
        using (var pedido = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{servidor.Porta}/api/v1/evento"))
        {
            pedido.Headers.TryAddWithoutValidation("X-Lixo", new string('b', 64_000));
            using var r = await _http.SendAsync(pedido);
            Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, r.StatusCode);
        }
        Assert.Single((await Evento(servidor)).Maquinas);
    }

    [Fact]
    public async Task Programacao_restaurada_com_o_painel_aberto_vale_na_hora()
    {
        // "Carregar programação" de outra máquina (backup com outro PIN) com o painel rodando: as vendas desta
        // máquina somem do painel e o PIN passa a ser o do backup
        var origem = Maquina(5, pin: "135790");
        var arquivo = Path.Combine(origem.Pasta, origem.Sistema.Programacao.NomeArquivo());
        origem.Sistema.Programacao.Salvar(arquivo);

        var a = Maquina(1);
        var sessao = a.Sistema.Caixa.Abrir(1, null, 0);
        Vender(a.Sistema, sessao, FormaPagamento.Pix, ("PASTEL", 2));
        var servidor = await Subir(a);
        Assert.Equal(2000, (await Evento(servidor)).TodoEvento.Vendido);

        a.Sistema.Programacao.Carregar(arquivo, 1);
        using (var antigo = await Pedir(servidor, "/api/v1/evento"))
            Assert.Equal(HttpStatusCode.Unauthorized, antigo.StatusCode);
        using var novo = await Pedir(servidor, "/api/v1/evento", "135790");
        Assert.Equal(HttpStatusCode.OK, novo.StatusCode);
        var evento = (await novo.Content.ReadFromJsonAsync<EstadoEvento>(Json.Opcoes))!;
        Assert.Equal((0L, 0, 0), (evento.TodoEvento.Vendido, evento.TodoEvento.Pedidos, evento.TodoEvento.Caixas));
    }

    [Fact]
    public async Task Muitos_celulares_olhando_ao_mesmo_tempo_sao_todos_atendidos()
    {
        // Cada celular deixa a conexão aberta entre uma pergunta e outra (a cada 3 s); o navegador abre mais de uma
        // ao carregar a página. A equipe toda com o QR code não pode deixar ninguém de fora.
        var a = Maquina(1);
        var servidor = await Subir(a);
        var celulares = Enumerable.Range(0, 48).Select(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(5) }).ToList();
        try
        {
            var respostas = await Task.WhenAll(celulares.Select(async c =>
            {
                try
                {
                    using var pedido = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{servidor.Porta}/api/v1/evento");
                    pedido.Headers.Add(Servidor.CabecalhoPin, Pin);
                    using var r = await c.SendAsync(pedido);
                    return r.StatusCode;
                }
                catch (HttpRequestException)
                {
                    return HttpStatusCode.ServiceUnavailable;
                }
            }));
            Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r));
        }
        finally
        {
            foreach (var c in celulares) c.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Os números do painel são os mesmos dos relatórios do caixa
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Numeros_do_painel_batem_com_os_relatorios_do_caixa()
    {
        var a = Maquina(1);
        var s = a.Sistema;

        // Ontem: caixa fechado, com sangria, suprimento e devolução
        var ontem = s.Caixa.Abrir(1, null, 5000);
        var p1 = Vender(s, ontem, FormaPagamento.Dinheiro, ("PASTEL", 2), ("CERVEJA", 3));
        Vender(s, ontem, FormaPagamento.Debito, ("ESPETINHO", 1));
        Vender(s, ontem, FormaPagamento.Credito, ("PASTEL", 1));
        s.Caixa.RegistrarMovimento(ontem, TipoMovimento.Suprimento, 2000, "troco");
        s.Caixa.RegistrarMovimento(ontem, TipoMovimento.Sangria, 3000, "cofre");
        s.Devolucoes.Devolver(ontem, p1.Id, new Dictionary<long, int> { [s.Vendas.Pedido(p1.Id)!.Itens[0].Id] = 1 }, "");
        s.Caixa.Fechar(ontem, 7000);

        // Modo teste: não entra em nada
        var teste = s.Caixa.Abrir(1, null, 0, teste: true);
        Vender(s, teste, FormaPagamento.Pix, ("PASTEL", 9));
        s.Caixa.Fechar(teste, null);

        // Hoje: caixa aberto, venda pendente na maquininha e venda cancelada não contam
        var hoje = s.Caixa.Abrir(1, null, 10000);
        Vender(s, hoje, FormaPagamento.Pix, ("PASTEL", 1), ("REFRIGERANTE", 2));
        var p2 = Vender(s, hoje, FormaPagamento.Dinheiro, ("BOLO", 4));
        Vender(s, hoje, FormaPagamento.Pix, ("CERVEJA", 1));
        var pendente = s.Vendas.CriarPedido(hoje, [new LinhaCarrinho { Produto = s.Catalogo.Produtos().First(p => p.Nome == "PASTEL"), Quantidade = 5 }],
            FormaPagamento.Credito);
        var cancelado = s.Vendas.CriarPedido(hoje, [new LinhaCarrinho { Produto = s.Catalogo.Produtos().First(p => p.Nome == "PASTEL"), Quantidade = 7 }],
            FormaPagamento.Debito);
        s.Vendas.Cancelar(cancelado.Id);
        s.Devolucoes.Devolver(hoje, p2.Id, new Dictionary<long, int> { [s.Vendas.Pedido(p2.Id)!.Itens[0].Id] = 2 }, "");
        s.Caixa.RegistrarMovimento(hoje, TipoMovimento.Sangria, 1000, "cofre");
        Assert.NotEqual(StatusPedido.Pago, s.Vendas.Pedido(pendente.Id)!.Status);

        var servidor = await Subir(a);
        var evento = await Evento(servidor);
        var maquina = Assert.Single(evento.Maquinas).Estado;

        var resumoHoje = s.Caixa.Resumo(hoje.Id);
        var resumoOntem = s.Caixa.Resumo(ontem.Id);
        Conferir(maquina.CaixaAberto, [resumoHoje]);
        Conferir(maquina.TodoEvento, [resumoOntem, resumoHoje]);
        Conferir(evento.CaixaAberto, [resumoHoje]);
        Conferir(evento.TodoEvento, [resumoOntem, resumoHoje]);
        Assert.Equal(resumoHoje.DinheiroEsperado, evento.TodoEvento.DinheiroNoCaixa); // só os abertos
        Assert.Equal(2, evento.TodoEvento.Caixas);
        Assert.Equal(1, evento.TodoEvento.CaixasAbertos);

        // Fechou o caixa de hoje e abriu outro: o "caixa aberto" passa a ser o novo, o evento todo tem os três
        s.Vendas.Cancelar(pendente.Id);
        s.Caixa.Fechar(hoje, null);
        var noite = s.Caixa.Abrir(1, null, 3000);
        Vender(s, noite, FormaPagamento.Credito, ("ESPETINHO", 2));
        evento = await Evento(servidor);
        Conferir(evento.CaixaAberto, [s.Caixa.Resumo(noite.Id)]);
        Conferir(evento.TodoEvento, [resumoOntem, s.Caixa.Resumo(hoje.Id), s.Caixa.Resumo(noite.Id)]);
    }

    /// <summary>O bloco do painel é a soma exata dos resumos do caixa (os mesmos dos relatórios).</summary>
    private static void Conferir(Bloco b, List<ResumoCaixa> resumos)
    {
        var vendido = resumos.Sum(r => r.TotalVendas);
        var pedidos = resumos.Sum(r => r.QuantidadePedidos);
        Assert.Equal(vendido, b.Vendido);
        Assert.Equal(resumos.Sum(r => r.TotalDevolvido), b.Devolvido);
        Assert.Equal(resumos.Sum(r => r.VendaLiquida), b.Liquido);
        Assert.Equal(pedidos, b.Pedidos);
        Assert.Equal(resumos.Sum(r => r.QuantidadeFichas), b.Fichas);
        Assert.Equal(pedidos == 0 ? 0 : vendido / pedidos, b.TicketMedio);
        Assert.Equal(resumos.Sum(r => r.Sangrias), b.Sangrias);
        Assert.Equal(resumos.Sum(r => r.Suprimentos), b.Suprimentos);
        Assert.Equal(resumos.Sum(r => r.QuantidadeDevolucoes), b.Devolucoes);
        Assert.Equal(resumos.Where(r => r.Sessao.Aberta).Sum(r => r.DinheiroEsperado), b.DinheiroNoCaixa);
        foreach (var forma in Enum.GetValues<FormaPagamento>())
        {
            var f = b.Formas.Single(x => x.Chave == Leitor.Chave(forma));
            Assert.Equal(resumos.Sum(r => r.Total(forma)), f.Valor);
            Assert.Equal(resumos.Sum(r => r.PedidosPorForma.GetValueOrDefault(forma)), f.Pedidos);
            Assert.Equal(resumos.Sum(r => r.Devolvido(forma)), f.Devolvido);
        }
        var produtos = resumos.SelectMany(r => r.Produtos).GroupBy(p => p.Nome)
            .ToDictionary(g => g.Key, g => (g.Sum(p => p.Quantidade), g.Sum(p => p.TotalCentavos)));
        Assert.Equal(produtos.Count, b.Produtos.Count);
        foreach (var p in b.Produtos) Assert.Equal(produtos[p.Nome], (p.Quantidade, p.Valor));
        Assert.Equal(b.Produtos.OrderByDescending(p => p.Quantidade).ThenBy(p => p.Nome).Select(p => p.Nome),
            b.Produtos.Select(p => p.Nome));
        // As horas somam exatamente o vendido e as vendas
        Assert.Equal(vendido, b.PorHora.Sum(h => h.Valor));
        Assert.Equal(pedidos, b.PorHora.Sum(h => h.Pedidos));
    }

    [Fact]
    public async Task Venda_nova_nao_faz_o_painel_somar_de_novo_os_caixas_ja_fechados()
    {
        // Máquina com muitos caixas fechados (várias festas): a cada venda nova, só o caixa aberto é lido de novo.
        // Antes, os 25 caixas eram somados a cada 3 s enquanto alguém olhava, e o tablet fraco sentia.
        var a = Maquina(1);
        var s = a.Sistema;
        MuitasVendas.Criar(s, dias: 25, pedidosPorDia: 600);
        var hoje = s.Caixa.Abrir(1, null, 0);
        Vender(s, hoje, FormaPagamento.Pix, ("PASTEL", 1));
        using (var aquecer = new Leitor(a.Pasta, "aquecer")) aquecer.Ler(); // o JIT não entra na conta
        var leitor = new Leitor(a.Pasta, "teste");
        try
        {
            var relogio = Stopwatch.StartNew();
            leitor.Ler();
            var primeira = relogio.Elapsed.TotalMilliseconds;

            var tempos = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                Vender(s, hoje, FormaPagamento.Dinheiro, ("CERVEJA", 1));
                relogio.Restart();
                var (estado, _) = leitor.Ler();
                tempos.Add(relogio.Elapsed.TotalMilliseconds);
                Assert.Equal(800 * (i + 1) + 1000, estado.CaixaAberto.Vendido);
            }
            Assert.True(tempos.Min() < primeira / 4,
                $"primeira leitura {primeira:0} ms, depois de cada venda {string.Join(", ", tempos.Select(t => t.ToString("0")))} ms");

            // E os números continuam os mesmos dos relatórios
            var (final, _) = leitor.Ler();
            var resumos = s.Caixa.Sessoes(new DateTime(2000, 1, 1), DateTime.Today.AddYears(1)).Select(x => s.Caixa.Resumo(x.Id)).ToList();
            Conferir(final.TodoEvento, resumos);
        }
        finally
        {
            leitor.Dispose();
        }
    }
}
