ore-silo-ui-title = Silo de matériaux
ore-silo-ui-label-clients = Machines
ore-silo-ui-label-mats = Matériaux
ore-silo-ui-magnet-enabled = Aimant : Activé
ore-silo-ui-magnet-disabled = Aimant : Désactivé
ore-silo-ui-itemlist-entry = {$linked ->
    [true] {"[Lié] "}
    *[False] {""}
} {$name} ({$beacon}) {$inRange ->
    [true] {""}
    *[false] (Hors de portée)
}
