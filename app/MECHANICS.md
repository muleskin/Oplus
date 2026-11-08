# Oplus — design sheet

The values and rules Oplus follows.

## Program layout
- One process: the window (control panel) and the sharing core (IPP server +
  mDNS responder on background threads).
- Single instance.
- Tray icon with menu Control Panel / About / Exit.
- `/hide` on the command line starts minimised to the tray.

## Printers and sharing
| what | real printer | folder / Dropbox printer |
|---|---|---|
| document formats (pdl) | `image/urf,image/jpeg` | `image/urf,application/pdf,image/jpeg` |
| colour | from driver | always yes |
| duplex | from driver | always yes |
| resolution | from driver (see below), 600 dpi when unknown | 600 dpi |
- Service type `_ipp._tcp,_universal` (subtype _universal).
- Maker "Oplus", model "Mobile Printer".
- TXT record: txtvers=1, qtotal=1, rp=<resource>, ty="<maker> <model>", adminurl,
  note=<location>, priority=40, product=(<model>), pdl=<formats>, Color=T/F, Duplex=T/F, usb_MFG,
  usb_MDL, air=username,password (only when the printer has a user name/password),
  URF=V1.4,RS<dpi>,[SRGB24,]W8,CP1,IS1,OB10,PQ3-4-5,MT1-2-3-4-5-6,DM3 (SRGB24 only when colour),
  Transparent=T, Binary=T, Bind=T, PaperCustom=F, Punch=0, UUID, kind=document,photo.
- Media list: iso_a4_210x297mm, iso_a5_148x210mm, iso_a6_105x148mm,
  iso_dl_110x220mm, na_legal_8.5x14in, na_letter_8.5x11in, na_number-10_4.125x9.5in,
  na_index-3x5_3x5in, oe_photo-l_3.5x5in, na_index-4x6_4x6in, na_5x7_5x7in.
- IPP operations: Print-Job, Validate-Job, Cancel-Job, Get-Job-Attributes, Get-Jobs,
  Get-Printer-Attributes.
- Only one job prints at a time ("Currently printing another job.").
- Security: HTTP Basic; one user name/password per printer; ':' is not allowed in the user name.

## Port
- Registry value `connect_port` = "flag=..,def_fir=..,def_sec=..,new=..".
- Defaults: flag=1 (use default port), first default 631, second default 12631, custom 12631.
- Custom port must be 1024-65535, otherwise 12631.

## Printing a job
- URF pages are turned into bitmaps and each page is stretched onto the whole printable area.
- Landscape when the page is wider than tall; copies through dmCopies.
- Duplex (DMDUP_HORIZONTAL=3) only when the job asked for two sides and the driver supports it,
  else simplex (1).
- Pages counted for the log: simplex = pages x copies; duplex = ceil(pages/2) x copies.
- JPEG jobs are loaded as a picture and printed the same way.
- If the printer was removed: log "Printting error! Printer removed".

## Saving to a folder
- File name: `YYYYMMDD-hhmmss-mmm-<job name>` + `.jpg` for JPEG or `.pdf` for PDF when the job name
  lacks that extension; job name characters \/:*|?"<> become spaces.
- URF jobs become a PDF with one picture per page (/DCTDecode).
- Log action "Folder" (or "Drop box"), page count 1.
- Dropbox printers use %USERPROFILE%\Dropbox, else %USERPROFILE%\My Documents\Dropbox, else you browse.

## Log
- %LocalAppData%\Oplus\newlog.csv.
- Header: Date,Action,Printer Name,User,MAC address,File Name,Page.
- Each line starts with local time `YYYYMMDD-hhmmss,`.
- When the file passes 1 GiB the next file is newlog-1.csv, newlog-2.csv, …
- Actions: Printing, Folder, Drop box, "Printting warning! Canceled job", "Printting error! Printer removed".

## Email alert
- One setting for all printers: MailServer, UserName, Password, SenderName, SendTo, Subject, Enable.
- Each field 1-32 characters, Subject 1-128. All fields empty = delete the setting.
- Alerts on printer states LOW TONER, PAUSED, PAPER OUT, PAPER JAM, TONER EMPTY, COVER OPEN, OTHER ERROR.

## Reading a Windows printer
- Printers come from HKCU\Software\Microsoft\Windows NT\CurrentVersion\Devices; network printers
  (`\\server\name`) are skipped.
- New printer: shared, alias = printer name, location = "Oplus@<computer name>".
- Port = PRINTER_INFO_2.pPortName.
- Colour: default DEVMODE dmColor 2 = colour, 1 = black and white.
- Duplex: DeviceCapabilities(DC_DUPLEX).
- Resolution: dmPrintQuality if it is 300, 600 or 1200, else GetDeviceCaps(LOGPIXELSX); then above 600,
  below 300 or not a multiple of 10 → 600.
- FormName: dmPaperSize 1 Letter, 4 Ledger, 5 Legal, 8 A3, 9 A4, 11 A5, 12 B4, 13 B5, 70 A6, 88 B6,
  otherwise dmFormName (31 chars).
- Anything that fails: no colour, no duplex, 600 dpi, "A4".
- Every time the list loads, stored printers get Port, FormName, Color, Duplex and Dpi re-read from the
  driver and are saved again when any of them changed. Printers that disappeared from Windows are
  removed with their Security key.

## Saved values per printer (HKCU\Software\Oplus)
- RealPrinterName, Alias, AliasLength (DWORD, characters), Location, Port, Color, Duplex, Dpi,
  FormName, IsFolder, FolderPath, Share; Security subkey with UserName + DPAPI-protected Password,
  rewritten each save.
- connect_port is written as REG_MULTI_SZ "flag=%d,def_fir=%d,def_sec=%d,new=%d,".

## Folder / Dropbox printers
- New name "Save to <name>", 1-256 chars, must be unique.

## Main window
| control | action |
|---|---|
| Share | share the selected printer with iPhone and iPad, on / off |
| Security | one user name/password per printer (each 1-32 chars, both empty = none) |
| Print queue | real printer: `rundll32 printui.dll,PrintUIEntry /o /n "<printer>"`; folder printer: open the folder |
| Edit | name + location become editable, Save / Cancel appear, list and add/remove buttons locked |
| Save | name 1-256 chars, location 1-256 chars (both required), name must be unique |
| Add | pop-up menu Save to Folder / Save to Dropbox |
| Remove | remove the selected folder printer at once (no question), with its Security key; real printers: nothing |
| Log | copy newlog.csv to %TEMP%\Oplus\YYYYMMDD-hhmmss-log.csv, write a log.htm table (title band #BBE094, header row #232584 with white text, cells #9E9E9F, a Save link to the CSV) and open it; "No Log file is available." when missing |
| Email Alert | email alert settings |
| Settings | launch on startup, Show/Hide Advance, default port "631 or 12631", custom port (1024 - 65535) |
| About | version |
| list click | show the printer's details |
| list double-click | same as Print queue |

- List background RGB(233,237,244); button face RGB(204,232,207).
- List columns: 35 px, 35 px, "Printer Name / Location" (rest), 35 px; 32x32 icons.
- Status refresh every 2 s.

## Not included
- Licence keys, trials, activation, shared-printer limits and online update checks: Oplus needs none.
- Bonjour is not required: Oplus has its own small mDNS responder.
