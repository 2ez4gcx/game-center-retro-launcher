// Màn hình chơi game của Game Center trên Android: chạy core libretro ngay trong app bằng LibretroDroid.
// Build ra player-release.aar; app .NET (src/GameCenter.Android) đóng gói file này và mở GameActivity bằng Intent.
plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.khuongdoan.gamecenter.player"
    compileSdk = 35

    ndkVersion = "28.2.13676358"

    defaultConfig {
        minSdk = 24
        ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
    }
    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.31.6"
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

val libretrodroid: Configuration by configurations.creating

dependencies {
    implementation("com.github.Swordfish90:LibretroDroid:0.13.2")
    libretrodroid("com.github.Swordfish90:LibretroDroid:0.13.2") { isTransitive = false }
    // Các thư viện dưới đây do app .NET cung cấp qua NuGet (Xamarin.AndroidX.*, Xamarin.Kotlin.StdLib)
    implementation("androidx.activity:activity-ktx:1.9.3")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
}

// Chép player.aar và libretrodroid.aar ra android/dist để app .NET đóng gói
tasks.register<Copy>("dist") {
    dependsOn("assembleRelease")
    from(layout.buildDirectory.file("outputs/aar/player-release.aar")) { rename { "player.aar" } }
    from(libretrodroid) { rename { "libretrodroid.aar" } }
    into(rootProject.layout.projectDirectory.dir("dist"))
}
