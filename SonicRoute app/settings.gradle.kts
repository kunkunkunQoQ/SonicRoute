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
    }
}

rootProject.name = "SonicRoute"
include(":app", ":core", ":platform")
if (providers.gradleProperty("routeFixture").isPresent) {
    include(":routefixture")
    project(":routefixture").projectDir = file("tools/route-fixture")
}
