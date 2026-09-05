# Bilješke proizvoda

## Background odabir aplikacije — 2026-09-05

- Trenutni CPU i Memory dropdown prikazuju samo aplikacije koje se mogu otvoriti iz Android launchera za trenutnog Android korisnika, primjerice YouTube, Photos i Postavke. Aplikacija ne mora trenutačno biti pokrenuta da bi bila na popisu.
- Sistemske aplikacije s vlastitim ekranom ostaju dostupne; pomoćni paketi, overlayi i servisi bez launcher aktivnosti nisu na popisu. Paket je naveden samo jednom, čak i ako ima više launcher aktivnosti.
- CPU i Memory dijele odabranu background aplikaciju i mjere njezine procese. Sam odabir ne pokreće aplikaciju.
- Na Memory kartici **A** prikazuje ukupni zauzeti RAM uređaja i ukupni kapacitet; chart prati zauzetost uređaja na skali 0–100%. **B** prikazuje RAM i chart samo odabrane aplikacije, bez obzira radi li na ekranu ili u pozadini. Promjena aktivnog ekrana ne mijenja aplikaciju odabranu za B.
- Ideja za buduću **Pro Developer** verziju: opcionalno omogućiti puni popis instaliranih paketa i procesa za naprednu dijagnostiku. Ta opcija zasad nije implementirana; zadani prikaz treba ostati jednostavan popis aplikacija.

## Launch application dropdown — 2026-09-05

- Prvi red otvorenog gornjeg dropdowna je zaglavlje **Launch application**, s kružnom ikonom **i** desno. Objašnjenje se prikazuje kad je miš iznad ikone; zaglavlje nije aplikacija koju se može odabrati.
- Popis sadrži aplikacije koje se mogu otvoriti. Klik na paket ili potvrda označene stavke tipkom Enter otvara aplikaciju na odabranom Android uređaju. Otvaranje ili zatvaranje popisa i programski odabir paketa sami ne pokreću aplikaciju.
