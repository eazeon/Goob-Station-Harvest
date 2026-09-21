# Body part translations
body-part-head = tête
body-part-chest = torse
body-part-groin = aine
body-part-leftarm = bras gauche
body-part-rightarm = bras droit
body-part-leftleg = jambe gauche
body-part-rightleg = jambe droite
body-part-lefthand = main gauche
body-part-righthand = main droite
body-part-leftfoot = pied gauche
body-part-rightfoot = pied droit
body-part-arm = bras
body-part-hand = main
body-part-leg = jambe
body-part-foot = pied

inspect-part-status-line = [font size=10]{$possessive} [bold]{
    $part ->
    [head] { body-part-head }
    [chest] { body-part-chest }
    [groin] { body-part-groin }
    [left_arm] { body-part-leftarm }
    [right_arm] { body-part-rightarm }
    [left_leg] { body-part-leftleg }
    [right_leg] { body-part-rightleg }
    [left_hand] { body-part-lefthand }
    [right_hand] { body-part-righthand }
    [left_foot] { body-part-leftfoot }
    [right_foot] { body-part-rightfoot }
    [arm] { body-part-arm }
    [hand] { body-part-hand }
    [leg] { body-part-leg }
    [foot] { body-part-foot }
    *[other] { $part }
}[/bold] est {$status}.[/font]
inspect-part-status-title = [font size = 11][color=DarkGray]Vous vous examinez pour détecter des blessures.[/color][/font]
inspect-part-status-title-other = [font size = 11][color=DarkGray]Vous examinez {$entity} pour détecter des blessures.[/color][/font]
inspect-part-status-line-styleless = {$possessive} {
    $part ->
    [head] { body-part-head }
    [chest] { body-part-chest }
    [groin] { body-part-groin }
    [left_arm] { body-part-leftarm }
    [right_arm] { body-part-rightarm }
    [left_leg] { body-part-leftleg }
    [right_leg] { body-part-rightleg }
    [left_hand] { body-part-lefthand }
    [right_hand] { body-part-righthand }
    [left_foot] { body-part-leftfoot }
    [right_foot] { body-part-rightfoot }
    [arm] { body-part-arm }
    [hand] { body-part-hand }
    [leg] { body-part-leg }
    [foot] { body-part-foot }
    *[other] { $part }
} est {$status}.
inspect-part-status-title-styleless = Vous vous examinez pour détecter des blessures.
inspect-part-status-title-other-styleless = Vous examinez {$entity} pour détecter des blessures.
inspect-part-status-fine = en bon état
inspect-part-status-comma = ,{" "}
inspect-part-status-conjunction = il semble être{" "}
inspect-part-status-conjunction2 = . Il est aussi{" "}
inspect-part-status-conjunction3 = Il{" "}
inspect-part-status-and = et{" "}
inspect-part-status-you = Votre
inspect-part-status-their = Son