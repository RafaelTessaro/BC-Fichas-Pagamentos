using SkiaSharp;

namespace BCFichas.Core.Impressao;

public sealed record ResultadoImpressao(bool Ok, string Mensagem);

/// <summary>Imprime fichas e relatórios no destino configurado (Elgin i9, porta COM ou arquivo).</summary>
public sealed class ServicoImpressao
{
    private readonly Func<Configuracao> _config;
    private readonly string _pastaDados;
    private readonly object _trava = new();
    private (string Caminho, DateTime Data)? _chaveLogo;
    private LogoFicha? _logo;

    public ServicoImpressao(Func<Configuracao> config, string pastaDados)
    {
        _config = config;
        _pastaDados = pastaDados;
    }

    /// <summary>Avisado depois de cada impressão (barra de status).</summary>
    public event Action<ResultadoImpressao>? Impresso;

    public ResultadoImpressao? Ultimo { get; private set; }

    public string PastaPadraoArquivo => Path.Combine(_pastaDados, "impressoes");

    public IDestinoImpressao CriarDestino(Configuracao c) => c.Impressora switch
    {
        TipoImpressora.Serial => new DestinoEscPos($"Porta {c.PortaSerial}",
            dados => TransporteSerial.Enviar(c.PortaSerial, c.BaudRate, dados), c.Corte),
        TipoImpressora.Arquivo => new DestinoArquivo(string.IsNullOrWhiteSpace(c.PastaArquivo) ? PastaPadraoArquivo : c.PastaArquivo),
        _ => new DestinoEscPos(string.IsNullOrWhiteSpace(c.NomeImpressora) ? "Impressora não escolhida" : c.NomeImpressora,
            dados => TransporteWindows.Enviar(c.NomeImpressora, dados), c.Corte),
    };

    public string Descricao() => CriarDestino(_config()).Descricao;

    public ResultadoImpressao Fichas(IReadOnlyList<Ficha> fichas)
    {
        if (fichas.Count == 0) return new ResultadoImpressao(true, "Nada para imprimir.");
        var config = _config();
        return Executar(config, () =>
        {
            var logo = Logo(config);
            return fichas.Select(f => RenderizadorFicha.Renderizar(f, config, logo)).ToList();
        }, fichas.Count == 1 ? "1 ficha impressa" : $"{fichas.Count} fichas impressas");
    }

    public ResultadoImpressao Documento(Documento documento, string descricao = "Relatório impresso")
    {
        var config = _config();
        return Executar(config, () => [documento.Renderizar(config.LarguraPontos)], descricao);
    }

    public ResultadoImpressao Teste(Configuracao config)
    {
        var destino = CriarDestino(config);
        return Executar(config, () =>
        {
            var logo = Logo(config);
            return
            [
                Relatorios.Teste(config, destino.Descricao).Renderizar(config.LarguraPontos),
                RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, logo),
            ];
        }, "Teste impresso");
    }

    /// <summary>Imagem da ficha em preto e branco, como vai sair no papel (prévia na tela).</summary>
    public SKBitmap Previa(Configuracao config)
    {
        lock (_trava)
        {
            using var colorida = RenderizadorFicha.Renderizar(RenderizadorFicha.Exemplo(config), config, Logo(config));
            return ImagemUtil.Monocromatico(colorida);
        }
    }

    /// <summary>Caminho absoluto de uma imagem guardada na pasta de dados.</summary>
    public string? CaminhoImagem(string? relativo) =>
        string.IsNullOrWhiteSpace(relativo) ? null
        : Path.IsPathRooted(relativo) ? relativo
        : Path.Combine(_pastaDados, relativo);

    private ResultadoImpressao Executar(Configuracao config, Func<List<SKBitmap>> paginas, string sucesso)
    {
        ResultadoImpressao resultado;
        lock (_trava)
        {
            List<SKBitmap>? lista = null;
            try
            {
                lista = paginas();
                CriarDestino(config).Imprimir(lista);
                resultado = new ResultadoImpressao(true, sucesso);
            }
            catch (ErroDeNegocio e)
            {
                resultado = new ResultadoImpressao(false, e.Message);
            }
            catch (Exception e)
            {
                resultado = new ResultadoImpressao(false, "Erro na impressora: " + e.Message);
            }
            finally
            {
                if (lista is not null)
                    foreach (var p in lista) p.Dispose();
            }
        }

        Ultimo = resultado;
        Impresso?.Invoke(resultado);
        return resultado;
    }

    /// <summary>Logo do evento (carregado uma vez só). Chamar sempre dentro de <see cref="_trava"/>.</summary>
    private LogoFicha? Logo(Configuracao config)
    {
        var caminho = CaminhoImagem(config.Logo);
        if (caminho is null || !File.Exists(caminho)) return null;

        var chave = (caminho, File.GetLastWriteTimeUtc(caminho));
        if (_chaveLogo == chave) return _logo;

        _logo?.Dispose();
        _logo = null;
        _chaveLogo = chave;
        var original = ImagemUtil.Carregar(caminho);
        if (original is null) return null;
        _logo = new LogoFicha(original);
        return _logo;
    }
}
