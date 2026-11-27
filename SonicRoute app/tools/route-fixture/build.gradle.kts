plugins { id("com.android.application") }
android {
    namespace = "org.sonicroute.fixture.route"
    compileSdk = rootProject.extra["androidCompileSdk"] as Int
    defaultConfig {
        applicationId = "org.sonicroute.fixture.route"
        minSdk = rootProject.extra["androidMinSdk"] as Int
        targetSdk = 35
        versionCode = 1
        versionName = "1"
    }
}
