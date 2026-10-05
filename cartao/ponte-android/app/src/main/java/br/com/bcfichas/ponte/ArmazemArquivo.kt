package br.com.bcfichas.ponte

import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream

/**
 * Os resultados das cobranças na memória do app (não usa cartão de memória). Guarda as últimas [MAXIMO]: o tablet
 * só pergunta das cobranças recentes.
 *
 * Grava em dois arquivos, um de cada vez, cada gravação com um número maior que a anterior. Se a maquininha desligar
 * no meio de uma gravação, o outro arquivo continua inteiro e é ele que vale ao abrir.
 */
class ArmazemArquivo(arquivo: File) : Armazem {
    private val vagas = arrayOf(File(arquivo.path + ".a"), File(arquivo.path + ".b"))
    private val trava = Any()
    private val resultados = LinkedHashMap<String, ResultadoPagamento>()
    private var emAndamento: PedidoPagamento? = null
    private var sequencia = 0L
    private var proximaVaga = 0

    init {
        ler()
    }

    override fun resultado(id: String): ResultadoPagamento? = synchronized(trava) { resultados[id] }

    override fun guardar(id: String, resultado: ResultadoPagamento) = synchronized(trava) {
        resultados.remove(id)
        resultados[id] = resultado
        while (resultados.size > MAXIMO) resultados.remove(resultados.keys.first())
        gravar()
    }

    override fun emAndamento(): PedidoPagamento? = synchronized(trava) { emAndamento }

    override fun marcarEmAndamento(pedido: PedidoPagamento?) = synchronized(trava) {
        emAndamento = pedido
        gravar()
    }

    override fun ultimas(quantas: Int): List<Pair<String, ResultadoPagamento>> =
        synchronized(trava) { resultados.entries.reversed().take(quantas).map { it.key to it.value } }

    private fun gravar() {
        val json = JSONObject().put("sequencia", sequencia + 1)
        val lista = JSONArray()
        for ((id, r) in resultados) lista.put(paraJson(r).put("id", id))
        json.put("resultados", lista)
        emAndamento?.let {
            json.put(
                "emAndamento", JSONObject().put("id", it.id).put("referencia", it.referencia).put("valor", it.valor)
                    .put("forma", it.forma).put("comprovante", it.comprovante)
            )
        }
        // Escreve por cima do arquivo mais velho e só então passa a valer (o mais novo fica intacto até a próxima)
        FileOutputStream(vagas[proximaVaga]).use { saida ->
            saida.write(json.toString().toByteArray(Charsets.UTF_8))
            saida.fd.sync()
        }
        sequencia++
        proximaVaga = 1 - proximaVaga
    }

    private fun ler() {
        var melhor: JSONObject? = null
        for ((vaga, arquivo) in vagas.withIndex()) {
            val json = try {
                JSONObject(arquivo.readText(Charsets.UTF_8))
            } catch (e: Exception) {
                continue // não existe ou ficou pela metade
            }
            if (melhor == null || json.optLong("sequencia") > melhor.optLong("sequencia")) {
                melhor = json
                proximaVaga = 1 - vaga
            }
        }
        val json = melhor ?: return
        try {
            sequencia = json.optLong("sequencia")
            val lista = json.optJSONArray("resultados") ?: JSONArray()
            for (i in 0 until lista.length()) {
                val r = lista.getJSONObject(i)
                resultados[r.getString("id")] = deJson(r)
            }
            json.optJSONObject("emAndamento")?.let {
                emAndamento = PedidoPagamento(
                    it.getString("id"), it.optString("referencia"), it.optLong("valor"), it.optString("forma"),
                    it.optBoolean("comprovante"),
                )
            }
        } catch (e: Exception) {
            // Arquivo estragado: começa com o que deu para ler (o tablet confere o que ficou sem resposta)
        }
    }

    companion object {
        const val MAXIMO = 500

        private fun paraJson(r: ResultadoPagamento) = JSONObject()
            .put("aprovado", r.aprovado).put("cancelado", r.cancelado).put("mensagem", r.mensagem)
            .putOpt("autorizacao", r.autorizacao).putOpt("nsu", r.nsu).putOpt("bandeira", r.bandeira)
            .putOpt("codigo", r.codigo).putOpt("referencia", r.referencia).putOpt("valor", r.valor)
            .put("indefinido", r.indefinido)

        private fun deJson(j: JSONObject) = ResultadoPagamento(
            aprovado = j.optBoolean("aprovado"), cancelado = j.optBoolean("cancelado"), mensagem = j.optString("mensagem"),
            autorizacao = j.opcional("autorizacao"), nsu = j.opcional("nsu"), bandeira = j.opcional("bandeira"),
            codigo = j.opcional("codigo"), referencia = j.opcional("referencia"),
            valor = if (j.has("valor") && !j.isNull("valor")) j.optLong("valor") else null,
            indefinido = j.optBoolean("indefinido"),
        )

        private fun JSONObject.opcional(chave: String): String? = if (has(chave) && !isNull(chave)) optString(chave) else null
    }
}
