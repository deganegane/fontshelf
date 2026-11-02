# FontShelf

Upravitelj fontova za Windows, po uzoru na Typeface s Maca.

## Što radi

- **Pregled i pretraga**: svi fontovi s tvojim tekstom, veličinom i prikazom kao redak, odlomak ili abeceda; pretraga po imenu, tagu i dizajneru (Ctrl+F)
- **Prikazi**: popis, mreža i tablica s metapodacima (verzija, dizajner, proizvođač…); sortiranje po imenu, broju stilova, datumu dodavanja ili favoritima
- **Favoriti i tagovi**: kolekcije u bočnoj traci, izvoz svih fontova s tagom u mapu
- **Usporedba**: stupci, preklapanje A/B i par naslov + tekst (pairing)
- **Tester**: OpenType značajke (ligature, stilski setovi, brojke…) i klizači za varijabilne osi
- **Glifovi**: svi znakovi; kopiranje znaka, koda ili svih znakova, izvoz znaka kao SVG ili PNG
- **Pametni filtri**: monospace, varijabilni, hrvatski znakovi (č ć đ š ž), duplikati
- **Mape i aktivacija**: dodaj mapu (ili je povuci u prozor) i aktiviraj cijeli font, cijelu mapu ili samo pojedine stilove; po želji privremena aktivacija do zatvaranja aplikacije; trajna instalacija bez administratorskih prava
- **Google Fonts**: pregledaj katalog i preuzmi fontove jednim klikom
- **Postavke**: skrivanje sistemskih fontova, svijetla i tamna tema

Postavke i tagovi spremaju se u `%APPDATA%\FontShelf\library.json`.

## Preuzimanje

Svaki push na `main` napravi novi build: **Releases → najnoviji `build-N` → `FontShelf.exe`**.
Aplikacija je jedna .exe datoteka, ne treba instalaciju ni .NET.

Napomena: Windows SmartScreen će možda upozoriti jer .exe nije potpisan — klikni „Više informacija → Ipak pokreni”.

## Build lokalno

```
dotnet publish src/FontShelf/FontShelf.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
