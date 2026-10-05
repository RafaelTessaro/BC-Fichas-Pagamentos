package br.com.bcfichas.ponte

import org.json.JSONObject
import java.util.UUID

/**
 * Uma mensagem entre o tablet e a maquininha: uma linha de JSON. É o mesmo formato do programa do tablet
 * (cartao/src/BCFichas.Core/Pagamento/ProtocoloPonte.cs).
 *
 * Tablet → maquininha: ola, cobrar, consultar, cancelar, ping.
 * Maquininha → tablet: ola, andamento, resultado, desconhecida, ocupada, erro, pong.
 */
class Mensagem(val campos: JSONObject) {
    val tipo: String get() = campos.optString("tipo")
    val id: String? get() = texto("id")
    val referencia: String? get() = texto("referencia")
    val forma: String? get() = texto("forma")
    val valor: Long? get() = if (campos.has("valor") && !campos.isNull("valor")) campos.optLong("valor") else null
    val caixa: Int? get() = if (campos.has("caixa") && !campos.isNull("caixa")) campos.optInt("caixa") else null
    val comprovante: Boolean get() = campos.optBoolean("comprovante", false)

    fun texto(chave: String): String? =
        if (campos.has(chave) && !campos.isNull(chave)) campos.optString(chave) else null

    fun linha(): String = campos.toString() + "\n"

    companion object {
        const val VERSAO = 1

        /** Serviço Bluetooth do app (o tablet liga direto nele, sem porta COM). */
        val SERVICO: UUID = UUID.fromString("b7c1f00d-5f1c-4d2e-9a3b-2f6c8e4a1b10")

        /** Porta serial padrão (SPP): para o tablet que preferir ligar por uma porta COM do Windows. */
        val SERIAL_PADRAO: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")

        /** Linha maior que isso não é do tablet: é descartada. */
        const val MAIOR_LINHA = 64 * 1024

        val FORMAS = setOf("debito", "credito", "pix")

        fun ler(linha: String): Mensagem? = try {
            val json = JSONObject(linha)
            if (json.optString("tipo").isNotEmpty()) Mensagem(json) else null
        } catch (e: Exception) {
            null
        }

        fun de(vararg pares: Pair<String, Any?>): Mensagem {
            val json = JSONObject()
            for ((chave, valor) in pares) if (valor != null) json.put(chave, valor)
            return Mensagem(json)
        }

        fun resultado(id: String, r: ResultadoPagamento) = de(
            "tipo" to "resultado", "id" to id, "aprovado" to r.aprovado, "cancelado" to r.cancelado,
            "mensagem" to r.mensagem, "autorizacao" to r.autorizacao, "nsu" to r.nsu, "bandeira" to r.bandeira,
            "codigo" to r.codigo,
        )

        fun andamento(id: String, texto: String) = de("tipo" to "andamento", "id" to id, "texto" to texto)

        /**
         * Código da venda para o PagBank: só letras (sem acento) e números, até 10. O tablet já manda pronto; se não
         * vier, usa as últimas 10 letras e números do identificador.
         */
        fun referenciaLimpa(referencia: String?, id: String): String {
            val pronta = (referencia ?: "").filter { it in 'A'..'Z' || it in 'a'..'z' || it in '0'..'9' }
            if (pronta.isNotEmpty()) return pronta.take(10)
            return id.filter { it in 'A'..'Z' || it in 'a'..'z' || it in '0'..'9' }.takeLast(10)
        }
    }
}
