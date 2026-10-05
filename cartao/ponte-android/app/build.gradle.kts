plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "br.com.bcfichas.ponte"
    compileSdk = 34

    defaultConfig {
        applicationId = "br.com.bcfichas.ponte"
        minSdk = 23
        // O PagBank pede minSdk e targetSdk 23 nos apps das maquininhas Smart (guia de boas práticas SmartPOS)
        targetSdk = 23
        versionCode = 1
        // Até 10 letras: o PagBank recusa nome de versão maior (INVALID_APP_VERSION)
        versionName = "1.0.0"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    lint {
        // targetSdk 23 é o que o PagBank pede, não a Google Play
        disable += setOf("ExpiredTargetSdkVersion", "OldTargetApi")
        abortOnError = false
        checkReleaseBuilds = false
    }

    testOptions {
        unitTests.isReturnDefaultValues = true
    }
}

dependencies {
    // Pagamento na própria maquininha (cartão e PIX)
    implementation("br.com.uol.pagseguro.plugpagservice.wrapper:wrapper:1.35.0")
    // O SDK do PagBank usa estas sem declarar: FileProvider (AndroidX) e RxJava 2
    implementation("androidx.core:core:1.12.0")
    implementation("io.reactivex.rxjava2:rxjava:2.2.21")
    implementation("io.reactivex.rxjava2:rxandroid:2.1.1")

    testImplementation("junit:junit:4.13.2")
    // O org.json do Android não funciona nos testes do computador
    testImplementation("org.json:json:20240303")
}
