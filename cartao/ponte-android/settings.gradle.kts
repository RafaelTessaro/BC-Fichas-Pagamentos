pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
        // Cópia local do SDK do PagBank (opcional: gradle -PrepositorioPagBank=file:///pasta/do/sdk)
        providers.gradleProperty("repositorioPagBank").orNull?.let { local -> maven { url = uri(local) } }
        // SDK do PagBank para as maquininhas Smart (PlugPagServiceWrapper): o endereço da documentação dele e o atual
        maven { url = uri("https://github.com/pagseguro/PlugPagServiceWrapper/raw/master") }
        maven { url = uri("https://github.com/pagseguro/pagseguro-sdk-plugpagservicewrapper/raw/master") }
    }
}

rootProject.name = "BCFichasPonte"
include(":app")
