using System.Globalization;
using BCFichas.Core;
using BCFichas.Core.Dados;
using BCFichas.Core.Servicos;
using Microsoft.Data.Sqlite;

namespace BCFichas.Painel;

/// <summary>
/// Lê as vendas desta máquina no banco do caixa, só para consulta. Guarda a última leitura: enquanto o caixa não
/// grava nada (PRAGMA data_version não muda), responde sem tocar no banco. Os caixas já fechados não mudam mais:
/// são somados uma vez só, e a cada venda nova só o caixa aberto é lido de novo.
/// </summary>
public sealed class Leitor : IDisposable
{
    private readonly string _arquivo;
    private readonly object _trava = new();
    private Banco? _banco;
    private SqliteConnection? _vigia;
    private long _versaoConfig = -1;
    private long _versaoLida = -1;
    /// <summary>Conta as vezes que a conexão de vigia foi aberta (o data_version recomeça a cada conexão).</summary>
    private int _conexao;
    private EstadoMaquina? _estado;
    private Configuracao _config = new();
    private readonly Dictionary<long, ParteDoCaixa> _fechados = new();

    public Leitor(string pastaDados, string instancia)
    {
        _arquivo = Path.Combine(pastaDados, "bcfichas.db");
        Instancia = instancia;
        Maquina = IdentidadeDaMaquina(pastaDados, instancia);
    }

    /// <summary>Muda a cada vez que o programa abre (separa duas máquinas com o mesmo número de caixa).</summary>
    public string Instancia { get; }

    /// <summary>
    /// A mesma enquanto for este tablet com este banco, mesmo que o programa feche e abra ou o roteador dê outro IP:
    /// é como o painel sabe que dois endereços são a mesma máquina (e não soma as vendas dela duas vezes).
    /// </summary>
    public string Maquina { get; }

    public static string Versao { get; } =
        typeof(Leitor).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>Configurações do caixa (PIN, máquinas da rede) como estavam na última leitura.</summary>
    public Configuracao Config
    {
        get
        {
            lock (_trava)
            {
                // Só o config (o programa confere a cada 5 s se o painel continua ligado): as vendas não são
                // somadas de novo se ninguém está olhando.
                Atualizar(estado: false);
                return _config;
            }
        }
    }

    /// <summary>Estado desta máquina e a marca dele (muda quando o caixa grava qualquer coisa).</summary>
    public (EstadoMaquina Estado, string Marca) Ler()
    {
        lock (_trava)
        {
            Atualizar(estado: true);
            // A conexão entra na marca: com uma conexão nova o data_version recomeça e repetiria uma marca antiga
            // (o celular receberia "nada mudou" com números velhos).
            return (_estado!, $"{Instancia}-{_conexao}-{_versaoLida}");
        }
    }

    private void Atualizar(bool estado)
    {
        try
        {
            _banco ??= Banco.SomenteLeitura(_arquivo);
            if (_vigia is null)
            {
                _vigia = _banco.Abrir();
                _conexao++;
            }
            var versao = Banco.Escalar<long>(_vigia, null, "PRAGMA data_version");
            if (versao != _versaoConfig)
            {
                _config = new ConfigServico(_banco).Atual;
                _versaoConfig = versao;
            }
            if (!estado || (versao == _versaoLida && _estado is not null)) return;
            _estado = LerEstado(_banco, _config);
            _versaoLida = versao;
        }
        catch (Exception e) when (e is SqliteException or IOException or InvalidOperationException or ErroDeNegocio)
        {
            // Caixa reabrindo o banco, arquivo ainda não criado, caixa apagado no meio da leitura ("Apagar as
            // vendas"): tenta de novo na próxima pergunta.
            _vigia?.Dispose();
            _vigia = null;
            _versaoLida = -1;
            _versaoConfig = -1;
            _fechados.Clear();
            _estado ??= new EstadoMaquina(Instancia, _config.NumeroCaixa, _config.NomeEvento, Versao, Agora(),
                Bloco.Vazio, Bloco.Vazio) { Maquina = Maquina };
            Registro.Erro("Ler o banco do caixa", e);
        }
    }

    /// <summary>O que o painel guarda de um caixa: o resumo (o mesmo dos relatórios), as vendas por hora e a última.</summary>
    private sealed record ParteDoCaixa(ResumoCaixa Resumo, List<HoraVendida> PorHora, string? UltimaVenda)
    {
        /// <summary>O caixa da lista ainda é este (mesmo número, abertura, troco e fechamento)?</summary>
        public bool Mesmo(SessaoCaixa s) =>
            Resumo.Sessao.Caixa == s.Caixa && Resumo.Sessao.AbertaEm == s.AbertaEm &&
            Resumo.Sessao.FechadaEm == s.FechadaEm && Resumo.Sessao.ValorAberturaCentavos == s.ValorAberturaCentavos &&
            Resumo.Sessao.ValorContadoCentavos == s.ValorContadoCentavos;
    }

    private EstadoMaquina LerEstado(Banco banco, Configuracao config)
    {
        var caixa = new CaixaServico(banco);
        // Caixas de verdade (os do modo teste ficam de fora, como nos relatórios do cliente)
        var sessoes = caixa.Sessoes(new DateTime(2000, 1, 1), DateTime.Today.AddYears(10)).OrderBy(s => s.Id).ToList();
        var partes = new List<ParteDoCaixa>(sessoes.Count);
        foreach (var s in sessoes)
        {
            // Caixa fechado não muda mais (devolução e sangria entram no caixa aberto): lido uma vez só
            if (!s.Aberta && _fechados.TryGetValue(s.Id, out var guardada) && guardada.Mesmo(s))
            {
                partes.Add(guardada);
                continue;
            }
            var parte = new ParteDoCaixa(caixa.Resumo(s.Id), PorHora(banco, s.Id), UltimaVenda(banco, s.Id));
            if (!s.Aberta) _fechados[s.Id] = parte;
            partes.Add(parte);
        }
        // Caixas que não existem mais ("Apagar as vendas", programação carregada)
        if (_fechados.Count > 0)
        {
            var ids = sessoes.Select(s => s.Id).ToHashSet();
            foreach (var id in _fechados.Keys.Where(id => !ids.Contains(id)).ToList()) _fechados.Remove(id);
        }
        var aberto = partes.Where(p => p.Resumo.Sessao.Aberta && p.Resumo.Sessao.Caixa == config.NumeroCaixa).ToList();
        return new EstadoMaquina(Instancia, config.NumeroCaixa, config.NomeEvento, Versao, Agora(),
            Somar(aberto), Somar(partes)) { Maquina = Maquina };
    }

    /// <summary>Soma os caixas desta máquina num bloco só.</summary>
    private static Bloco Somar(IReadOnlyList<ParteDoCaixa> partes)
    {
        if (partes.Count == 0) return Bloco.Vazio;
        var resumos = partes.Select(p => p.Resumo).ToList();
        var pedidos = resumos.Sum(r => r.QuantidadePedidos);
        var vendido = resumos.Sum(r => r.TotalVendas);
        var abertos = resumos.Where(r => r.Sessao.Aberta).ToList();
        return new Bloco
        {
            Vendido = vendido,
            Devolvido = resumos.Sum(r => r.TotalDevolvido),
            Liquido = resumos.Sum(r => r.VendaLiquida),
            Pedidos = pedidos,
            Fichas = resumos.Sum(r => r.QuantidadeFichas),
            TicketMedio = ResumoCaixa.Media(vendido, pedidos),
            Sangrias = resumos.Sum(r => r.Sangrias),
            Suprimentos = resumos.Sum(r => r.Suprimentos),
            Devolucoes = resumos.Sum(r => r.QuantidadeDevolucoes),
            DinheiroNoCaixa = abertos.Sum(r => r.DinheiroEsperado),
            Caixas = resumos.Count,
            CaixasAbertos = abertos.Count,
            AbertoEm = abertos.Count == 0 ? null : Data(abertos.Min(r => r.Sessao.AbertaEm)),
            UltimaVendaEm = partes.Select(p => p.UltimaVenda).Where(u => u is not null).Max(StringComparer.Ordinal),
            Formas = Enum.GetValues<FormaPagamento>().Select(f => new FormaVendida(Chave(f), Nomes.De(f),
                    resumos.Sum(r => r.Total(f)), resumos.Sum(r => r.PedidosPorForma.GetValueOrDefault(f)),
                    resumos.Sum(r => r.Devolvido(f))))
                .ToList(),
            Produtos = resumos.SelectMany(r => r.Produtos)
                .GroupBy(p => p.Nome)
                .Select(g => new ProdutoVendidoPainel(g.Key, g.Sum(p => p.Quantidade), g.Sum(p => p.TotalCentavos)))
                .OrderByDescending(p => p.Quantidade).ThenBy(p => p.Nome)
                .ToList(),
            PorHora = partes.SelectMany(p => p.PorHora).GroupBy(h => h.Hora)
                .Select(g => new HoraVendida(g.Key, g.Sum(h => h.Pedidos), g.Sum(h => h.Valor)))
                .OrderBy(h => h.Hora, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static string? UltimaVenda(Banco banco, long sessao) =>
        banco.Escalar<string>("SELECT MAX(criado_em) FROM pedidos WHERE status = $pago AND sessao_id = $s",
            ("$pago", (int)StatusPedido.Pago), ("$s", sessao)) is { } texto ? texto.Replace(' ', 'T') : null;

    private static List<HoraVendida> PorHora(Banco banco, long sessao) =>
        banco.Consultar("""
            SELECT substr(criado_em, 1, 13), COUNT(*), SUM(total) FROM pedidos
            WHERE status = $pago AND sessao_id = $s
            GROUP BY 1 ORDER BY 1
            """, l => new HoraVendida(l.GetString(0), l.GetInt32(1), l.GetInt64(2)),
            ("$pago", (int)StatusPedido.Pago), ("$s", sessao));

    /// <summary>
    /// Identidade deste tablet com este banco: um número guardado na pasta de dados (painel-maquina.id) junto com
    /// o nome do computador (a pasta copiada para outro tablet não vira a mesma máquina). Sem poder gravar na pasta,
    /// vale só enquanto o programa está aberto.
    /// </summary>
    private static string IdentidadeDaMaquina(string pastaDados, string instancia)
    {
        var numero = instancia;
        try
        {
            var arquivo = Path.Combine(pastaDados, "painel-maquina.id");
            var guardado = File.Exists(arquivo) ? File.ReadAllText(arquivo).Trim() : "";
            if (guardado.Length is < 16 or > 64 || !guardado.All(char.IsAsciiHexDigit))
            {
                guardado = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(pastaDados);
                File.WriteAllText(arquivo, guardado);
            }
            numero = guardado;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Pasta só de leitura: fica a identidade desta abertura do programa
        }
        return Servidor.Resumo(Environment.MachineName + "/" + numero);
    }

    internal static string Chave(FormaPagamento forma) => forma switch
    {
        FormaPagamento.Dinheiro => "dinheiro",
        FormaPagamento.Debito => "debito",
        FormaPagamento.Credito => "credito",
        FormaPagamento.Pix => "pix",
        _ => forma.ToString().ToLowerInvariant(),
    };

    internal static string Data(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    internal static string Agora() => Data(DateTime.Now);

    public void Dispose()
    {
        lock (_trava)
        {
            _vigia?.Dispose();
            _vigia = null;
        }
    }
}
