package br.com.bcfichas.ponte

import android.app.Activity
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/**
 * A tela do app na maquininha: mostra se o tablet está ligado, as últimas cobranças e o botão para parear com o
 * tablet. As cobranças em si acontecem no [ServicoPonte], mesmo com esta tela fechada.
 */
class TelaPrincipal : Activity() {
    private lateinit var situacao: TextView
    private lateinit var bluetooth: TextView
    private lateinit var ultimas: TextView
    private lateinit var ligarBluetooth: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ServicoPonte.iniciar(this)
        setContentView(montar())
    }

    override fun onResume() {
        super.onResume()
        ServicoPonte.aoMudar = { texto, armazem -> runOnUiThread { mostrar(texto, armazem) } }
        mostrar(ServicoPonte.situacaoAtual, ServicoPonte.ultimoArmazem)
    }

    override fun onPause() {
        ServicoPonte.aoMudar = null
        super.onPause()
    }

    private fun mostrar(texto: String, armazem: Armazem?) {
        situacao.text = texto
        val adaptador = getSystemService(BluetoothManager::class.java)?.adapter
        val ativo = adaptador?.isEnabled == true
        bluetooth.text = when {
            adaptador == null -> "Esta maquininha não tem Bluetooth."
            ativo -> "Bluetooth ligado" + (adaptador.name?.let { " • nome: $it" } ?: "")
            else -> "Bluetooth desligado: ligue para o tablet achar a maquininha."
        }
        ligarBluetooth.visibility = if (adaptador != null && !ativo) Button.VISIBLE else Button.GONE
        val lista = armazem?.ultimas(5).orEmpty()
        ultimas.text = if (lista.isEmpty()) "Nenhuma cobrança ainda."
        else lista.joinToString("\n") { (id, r) ->
            val pedido = id.substringAfter("-P").substringBefore("-").trimStart('0').ifEmpty { "0" }
            "Pedido $pedido: " + when {
                r.aprovado -> "aprovado" + (r.bandeira?.let { " ($it)" } ?: "")
                r.indefinido -> "sem confirmação do PagBank (conferir)"
                r.cancelado -> "cancelado"
                else -> "não aprovado"
            }
        }
    }

    private fun montar(): ScrollView {
        val coluna = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(24), dp(20), dp(24))
            setBackgroundColor(Color.parseColor("#F1F5F9"))
        }
        coluna.addView(TextView(this).apply {
            text = "BC-FICHAS"
            setTextColor(Color.parseColor("#16A34A"))
            textSize = 14f
            typeface = Typeface.DEFAULT_BOLD
            letterSpacing = 0.1f
        })
        coluna.addView(TextView(this).apply {
            text = "Maquininha do caixa"
            setTextColor(Color.parseColor("#0F172A"))
            textSize = 24f
            typeface = Typeface.DEFAULT_BOLD
        })
        situacao = cartao(coluna, "Situação", 22f, true)
        bluetooth = cartao(coluna, "Bluetooth", 16f, false)
        ligarBluetooth = botao(coluna, "Ligar o Bluetooth", "#0F172A") {
            startActivity(Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE))
        }
        botao(coluna, "Deixar visível para o tablet", "#16A34A") {
            startActivity(Intent(BluetoothAdapter.ACTION_REQUEST_DISCOVERABLE)
                .putExtra(BluetoothAdapter.EXTRA_DISCOVERABLE_DURATION, 300))
        }
        coluna.addView(TextView(this).apply {
            text = "Para parear: toque em Deixar visível, depois no tablet vá em Configurações do Windows → " +
                "Bluetooth → Adicionar e escolha esta maquininha. Confirme o mesmo código nos dois.\n\n" +
                "Deixe este app aberto (ou em segundo plano) durante o evento: o valor vem do tablet sozinho e o " +
                "cliente paga aqui, no cartão ou no PIX."
            setTextColor(Color.parseColor("#475569"))
            textSize = 14f
            setPadding(0, dp(8), 0, dp(8))
        })
        ultimas = cartao(coluna, "Últimas cobranças", 15f, false)
        return ScrollView(this).apply { addView(coluna, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT)) }
    }

    private fun cartao(pai: LinearLayout, titulo: String, tamanho: Float, negrito: Boolean): TextView {
        val caixa = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(12), dp(16), dp(14))
            background = GradientDrawable().apply {
                setColor(Color.WHITE)
                cornerRadius = dp(14).toFloat()
            }
        }
        caixa.addView(TextView(this).apply {
            text = titulo
            setTextColor(Color.parseColor("#64748B"))
            textSize = 13f
        })
        val valor = TextView(this).apply {
            setTextColor(Color.parseColor("#0F172A"))
            textSize = tamanho
            if (negrito) typeface = Typeface.DEFAULT_BOLD
        }
        caixa.addView(valor)
        pai.addView(caixa, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { topMargin = dp(12) })
        return valor
    }

    private fun botao(pai: LinearLayout, texto: String, cor: String, acao: () -> Unit): Button {
        val b = Button(this).apply {
            text = texto
            setTextColor(Color.WHITE)
            textSize = 17f
            isAllCaps = false
            gravity = Gravity.CENTER
            background = GradientDrawable().apply {
                setColor(Color.parseColor(cor))
                cornerRadius = dp(14).toFloat()
            }
            setOnClickListener { acao() }
        }
        pai.addView(b, LinearLayout.LayoutParams(MATCH_PARENT, dp(56)).apply { topMargin = dp(12) })
        return b
    }

    private fun dp(valor: Int) = (valor * resources.displayMetrics.density).toInt()
}
