# FontShelf

Upravitelj fontova za Windows, po uzoru na Typeface s Maca.

## Što radi

- **Pregled i pretraga** — svi instalirani fontovi s tvojim probnim tekstom i podesivom veličinom; pretraga po imenu i tagovima (Ctrl+F)
- **Favoriti i tagovi** — označi fontove zvjezdicom i dodaj im vlastite tagove (npr. „logo”, „meni”); tagovi su u bočnoj traci kao kolekcije
- **Usporedba** — dodaj fontove u usporedbu i vidi isti tekst jedan do drugoga
- **Glifovi** — mreža svih znakova fonta, klik za veliki prikaz i kopiranje znaka ili koda
- **Pametni filtri** — monospace fontovi i fontovi koji imaju sva hrvatska slova (č ć đ š ž)
- **Mape i aktivacija** — dodaj mapu s fontovima (ili je povuci u prozor), pregledaj ih bez instalacije i aktiviraj jednim klikom da budu dostupni u svim aplikacijama; „Instaliraj trajno” ih instalira za tvoj korisnički račun bez administratorskih prava

Postavke i tagovi spremaju se u `%APPDATA%\FontShelf\library.json`.

## Preuzimanje

Svaki push na `main` napravi novi build: **Releases → najnoviji `build-N` → `FontShelf.exe`**.
Aplikacija je jedna .exe datoteka, ne treba instalaciju ni .NET.

Napomena: Windows SmartScreen će možda upozoriti jer .exe nije potpisan — klikni „Više informacija → Ipak pokreni”.

## Build lokalno

```
dotnet publish src/FontShelf/FontShelf.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
