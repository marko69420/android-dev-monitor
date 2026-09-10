# Android Dev Monitor companion SDK (opcionalno)

Mala, opcionalna biblioteka koja vašoj aplikaciji omogućuje da pošalje vlastite performance markere u Android Dev Monitor. Ako je ne dodate, sve ostale funkcije alata rade normalno.

## Što dobivate

| Poziv | Namjena |
|---|---|
| `AdmCompanion.mark("app start")` | Poslovni ili UX događaj u vremenskoj crti. |
| `AdmCompanion.metric("feed_items", 42, "items")` | Brojčana vrijednost s jedinicom. |
| `AdmCompanion.duration("checkout", 412)` | Trajanje u milisekundama. |
| `AdmCompanion.event("workmanager", "sync", "ok", null, "constraint met")` | Bilo koji vlastiti događaj s dodatnim tekstom. |

Svi događaji izlaze kao linije s oznakom `ADM_COMPANION` u logcatu, a alat ih pretvara u izvještaj **Companion event report** (Developer Tools → Diagnostics & profiling) sa sažetkom po tipu događaja i vremenskom crtom.

## Ugradnja u tri koraka

1. Kopirajte `src/main/java/com/androiddevmonitor/companion/AdmCompanion.java` u svoj projekt, na primjer u `app/src/main/java/com/androiddevmonitor/companion/`.
2. Pozovite metode na mjestima koja mjerite.
3. Pokrenite aplikaciju, odaberite uređaj u Android Dev Monitoru i pokrenite **Companion event report**.

## Primjer

```java
AdmCompanion.mark("app start");
long started = System.currentTimeMillis();
loadFeed();
AdmCompanion.duration("feed load", System.currentTimeMillis() - started);
AdmCompanion.metric("feed_items", items.size(), "items");
```

Iz Kotlina isto radi bez izmjena:

```kotlin
AdmCompanion.mark("app start")
AdmCompanion.duration("checkout", 412)
```

## Format linije

```
ADM_COMPANION|<kind>|<name>|<value>|<unit>|<payload>
```

Prazna polja su dopuštena (`ADM_COMPANION|mark|feed loaded`). Znak `|` unutar vrijednosti zamjenjuje se s `/`.

## Napomena o trošku

`Log.i` je jeftin, ali nemojte zvati SDK u petljama s tisućama iteracija. Tipično je dovoljno 10–50 markera po ekranu.
