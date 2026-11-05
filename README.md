# ScrcpyZ

Pokretač za scrcpy koji pronalazi telefon po imenu (mDNS). Ne morate da upisujete IP adresu.

## Preuzimanje
Na stranici **Releases** preuzmi `ScrcpyZ-x.y.z-win64.zip`, raspakuj ga bilo gde i pokreni `ScrcpyZ.exe`.
U zipu su ScrcpyZ i scrcpy (sa adb-om). Ništa se ne instalira, a ne treba ni .NET.
Za ažuriranje je dovoljno zameniti `ScrcpyZ.exe` (iz istog Releasea).

scrcpy je delo Genymobile-a, licenca Apache 2.0 (`LICENSE-scrcpy.txt`).

## Build (ručno)
Pokrenite `build.bat` (potreban je .NET 10 SDK). Gotov program je `publish\ScrcpyZ.exe`.
Svaki push na GitHub automatski pravi novi Release.

## Prvo pokretanje
1. Ako koristite zip iz Releases, scrcpy je već tu. Inače kopirajte ScrcpyZ.exe u scrcpy folder ili izaberite folder klikom na „Folder scrcpy-ja…“.
2. Na telefonu uključite Wireless debugging, kliknite „Upari telefon…“ i unesite IP:port i kod. Ovo se radi samo jednom.
3. Izaberite telefon, kliknite „★ Omiljeni“, a zatim „Poveži“.

Ako je uključena opcija „Automatski poveži omiljeni telefon“, scrcpy će se ubuduće sam pokrenuti čim se telefon pojavi na mreži.
Podešavanja se čuvaju u `%AppData%\ScrcpyZ\settings.json`.

## Otključavanje i gašenje ekrana (1.1)
- Upišite PIN telefona u opcijama. Čuva se šifrovano (Windows DPAPI). Dugme „🔓 Otključaj“ budi telefon, prevlači preko zaključanog ekrana i upisuje PIN. Ako je uključena opcija „Otključaj telefon pri povezivanju“, to se radi automatski pre pokretanja scrcpy-ja. Šablon (pattern) nije podržan, samo PIN i lozinka.
- Kada je uključena opcija „Ugasi ekran telefona“, ScrcpyZ posle pokretanja sam šalje Alt+O prozoru scrcpy-ja. Dugme „Ugasi ekran“ radi isto ručno. Alt+Shift+O ponovo pali ekran.

## Šablon / pattern (1.2)
U opcijama izaberite „Šablon (pattern)“ i kliknite „Podesi šablon…“:
1. mišem nacrtajte svoj šablon,
2. zaključajte telefon i kliknite „Pronađi tačke na telefonu“. Program sam pronalazi tačke (uiautomator), a ako to ne uspe, poravnajte ih klizačima,
3. kliknite „Probaj na telefonu“. Kada se telefon otključa, kliknite „Sačuvaj“.
Šablon se crta jednim potezom preko `input motionevent`, za šta je potreban Android 11 ili noviji.

## Tray (1.5)
Kada se otvori prozor scrcpy-ja, ScrcpyZ se skloni u tray (pored sata). Isto se dešava i kad ga minimizuješ dok scrcpy radi.
- Klik na ikonu vraća ScrcpyZ.
- Desni klik nudi: Otvori ScrcpyZ, Prikaži scrcpy prozor, Otključaj telefon, Ugasi ekran telefona, Zatvori ScrcpyZ.
- Kad se scrcpy zatvori, ScrcpyZ se sam vrati na ekran.
- Isključuje se opcijom „Posle povezivanja idi u tray“.
