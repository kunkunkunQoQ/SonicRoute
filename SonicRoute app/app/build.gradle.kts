plugins {
    id("com.android.application")
    kotlin("android")
}

android {
    namespace = "org.sonicroute.app"
    compileSdk = rootProject.extra["androidCompileSdk"] as Int
    buildToolsVersion = rootProject.extra["androidBuildTools"] as String
    defaultConfig {
        applicationId = "org.sonicroute.app"
        minSdk = rootProject.extra["androidMinSdk"] as Int
        targetSdk = 35
        testInstrumentationRunner = "org.sonicroute.app.test.LanInteropRunner"
        versionCode = 1
        versionName = "0.1.0"
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies { implementation(project(":platform")) }
