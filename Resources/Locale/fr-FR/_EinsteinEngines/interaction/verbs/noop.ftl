interaction-LookAt-name = Fixer
interaction-LookAt-description = Regardez dans le vide et voyez-le vous regarder en retour.
interaction-LookAt-success-self-popup = Vous fixez {THE($target)}.
interaction-LookAt-success-target-popup = Vous sentez {THE($user)} vous fixer...
interaction-LookAt-success-others-popup = {THE($user)} fixe {THE($target)}.

interaction-Hug-name = Câliner
interaction-Hug-description = Un câlin par jour éloigne les horreurs psychologiques au-delà de votre compréhension.
interaction-Hug-success-self-popup = Vous faites un câlin à {THE($target)}.
interaction-Hug-success-target-popup = {THE($user)} vous fait un câlin.
interaction-Hug-success-others-popup = {THE($user)} fais un câlin {THE($target)}.

interaction-KnockOn-name = Frapper
interaction-KnockOn-description = Frapper sur la cible pour attirer l'attention.
interaction-KnockOn-success-self-popup = Vous frappez sur {THE($target)}.
interaction-KnockOn-success-target-popup = {THE($user)} frappe sur vous.
interaction-KnockOn-success-others-popup = {THE($user)} frappe sur {THE($target)}.

# The below includes conditionals for if the user is holding an item
interaction-WaveAt-name = Saluer
interaction-WaveAt-description = Saluer la cible. Si vous tenez un objet, vous l'agitiez.
interaction-WaveAt-success-self-popup = Vous saluez {$hasUsed ->
    [false] {THE($target)}.
    *[true] votre {$used} à {THE($target)}.
}
interaction-WaveAt-success-target-popup = {THE($user)} vous salue {$hasUsed ->
    [false] .
    *[true] en agitant votre {$used}.
}
interaction-WaveAt-success-others-popup = {THE($user)} salut {THE($target)} {$hasUsed ->
    [false] .
    *[true] en agitant {POSS-PRONOUN($user)} {$used}.
}
