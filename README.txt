NAMP 1.0.0 - free local web server for Windows, by Lvato (https://lvato.com)
============================================================================

Apache 2.4.71 + PHP 8.5.13 / 8.3.37 + MariaDB 11.8.10 LTS + phpMyAdmin 5.2.3

START
  Double-click "NAMP" on the desktop. It runs as a normal user - no
  administrator prompt. The web server and database start automatically.
  Closing the window stops everything safely.

  If XAMPP is running, stop its Apache/MySQL first (ports 80, 443 and 3306),
  or change NAMP's ports in Settings.

ADDRESSES
  Websites:     http://localhost  and  https://localhost   -> C:\NAMP\www
  phpMyAdmin:   https://localhost/phpmyadmin/
  phpinfo():    https://localhost/namp/phpinfo.php

  PHP database connection:
     host 127.0.0.1 (or localhost), port 3306, user root, empty password

SITES (.local domains with HTTPS)
  Sites page -> type "myshop" (becomes myshop.local), pick the project folder
  (or leave it empty to get C:\NAMP\www\myshop) -> Add site.
  Open https://myshop.local - the certificate is trusted automatically.
  Allowed endings: .local .test .localhost .lan .internal .home.arpa
  (real internet endings like .com are refused so you can't hide a real site).

PHP VERSION
  Settings -> PHP version: 8.5 (default, latest) or 8.3. Apache restarts with
  the chosen version. Each version has its own php.ini in C:\NAMP\php\<version>.

POWER-LOSS PROTECTION
  - Every COMMIT is flushed to disk before it is confirmed; the doublewrite
    buffer protects against half-written pages.
  - MariaDB runs as the Windows service "NAMP-MariaDB": Windows stops it
    cleanly at shutdown even if NAMP was closed or killed.
  - After a power cut NAMP repairs automatically when the database starts:
    log cleanup -> table repair -> safe-mode export + clean rebuild ->
    last-good copy -> SQL backups. Damaged files are moved to
    C:\NAMP\backups\quarantine, never deleted.
  - A last-good copy of the database files is saved every time MariaDB stops
    cleanly, and every database is backed up as SQL every 60 minutes
    (unchanged ones are skipped, 10 versions kept). Restore from Databases.

HOW IT WORKS WITHOUT ADMIN RIGHTS
  The installer sets up two Windows services once:
  - NAMP-MariaDB  the database (runs under its own low-privilege account)
  - NAMP-Helper   keeps the hosts file and the HTTPS certificates up to date
                  for your local sites. It only ever accepts local names.
  NAMP's HTTPS certificate authority can only issue certificates for local
  names; the key of its root certificate is destroyed right after setup.
  Program folders are read-only for normal users; www, data, logs, backups
  and config are writable.

FOLDERS
  C:\NAMP\www                       default website
  C:\NAMP\data\mariadb              database files
  C:\NAMP\backups                   snapshots, SQL backups, quarantine
  C:\NAMP\logs                      Apache / MariaDB / PHP / per-site logs
  C:\NAMP\php\8.5\php.ini           PHP 8.5 settings
  C:\NAMP\apache\conf\httpd.conf    Apache settings
  C:\NAMP\config\service\my-custom.ini   your MariaDB settings
                                    (edit as administrator - the database
                                    service reads it)

UNINSTALL
  Windows Settings -> Apps -> NAMP. The services, the HTTPS certificate and the
  hosts entries are removed; your websites, databases and backups in C:\NAMP
  are kept.

THIRD-PARTY SOFTWARE
  NAMP bundles software from these projects. Their license files remain in the
  installed component folders and continue to govern those components:

  - Apache HTTP Server 2.4.71 — Apache License 2.0
    Source: https://httpd.apache.org/download.cgi
  - PHP 8.5.13 and 8.3.37 — PHP License
    Source: https://www.php.net/downloads.php
  - MariaDB 11.8.10 LTS — GNU GPL v2
    Source: https://github.com/MariaDB/server
  - phpMyAdmin 5.2.3 — GNU GPL v2
    Source: https://www.phpmyadmin.net/downloads/

PROJECT
  Source code and releases: https://github.com/abdulrehman-mr/namp
