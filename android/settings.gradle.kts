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
        // LibretroDroid (GPL-3.0) phát hành qua JitPack
        maven { url = uri("https://jitpack.io") }
    }
}
rootProject.name = "gamecenter-android"
include(":player")
