Fonts shipped with ReportStudio
===============================

Put the Ubuntu font files here, exactly these four names:

    Ubuntu-Regular.ttf
    Ubuntu-Bold.ttf
    Ubuntu-Italic.ttf
    Ubuntu-BoldItalic.ttf

Get them from https://design.ubuntu.com/font (Ubuntu Font Family) and keep the
licence file (UFL.txt) next to them. The Ubuntu Font Licence allows shipping
the fonts with an application.

At build time every *.ttf here is copied to the "Fonts" folder next to the
Designer, the Viewer and your own application. At run time:

  * the screen, print and image output use these files, even when the font is
    not installed on the machine;
  * the PDF embeds every font it uses (as a subset), so it looks the same everywhere.

File names decide the style: <Family>-Regular / -Bold / -Italic / -BoldItalic.
Other families can be added the same way, or from code with
FontCatalog.AddDirectory(path). A font that is not available falls back to
Ubuntu in the PDF.
