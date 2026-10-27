# SPDX-License-Identifier: AGPL-3.0-or-later

roles-antag-gang-leader-name = Chef de Gang
roles-antag-gang-leader-objective = Prouve que tu as encore ce qu'il faut. Revendique des territoires, accumule des points et écrase tous les gangs rivaux qui se dressent sur ton chemin.

roles-antag-gang-name = Membre de Gang
roles-antag-gang-objective = Soutiens ton chef, garde ton territoire et aide ton gang à dominer la station avant que les rivaux ne le fassent.

gang-leader-role-greeting =
    Tu es un Chef de Gang.
    Tes patrons ne sont pas contents. Tes échecs récents t'ont mis sur la sellette, et la patience a des limites.
    C'est ta dernière chance de prouver ta valeur. Échoue encore et il n'y en aura pas d'autre.
    Frappe la station fort. Recrute qui tu peux, revendique chaque coin possible, et fais comprendre qui dirige cet endroit.
    Ne te sens pas trop à l'aise, cependant. On dit que des gangs rivaux sont déjà en mouvement avec la même idée.

gang-leader-action-summon-locker-name = Invoquer le Casiers du Gang
gang-leader-action-summon-locker-desc = Invoque ton casier de gang à ton emplacement. S'il est déjà placé, le déplace à la place.

gang-member-action-toggle-overlay-name = Basculer la Superposition de Territoire
gang-member-action-toggle-overlay-desc = Afficher ou masquer la superposition de territoire.

gang-leader-locker-summoned = Ton casier arrive !

gang-leader-member-accepted = Ton invitation de gang a été acceptée.
gang-leader-member-denied = Ton invitation de gang a été refusée.
gang-invite-target-already-member = Cette personne est déjà dans un gang.
gang-invite-sent = {$name} a été invité ! En attente de sa réponse.
gang-invite-already-outgoing = Tu as déjà une invitation en attente !
gang-invite-target-invalid = Cette personne ne peut pas rejoindre un gang !

gang-invite-window-title = Invitation de Gang
gang-invite-window-prompt = {$leader} veut que tu rejoignes les [color=#{$color}]{$gangName}[/color] !
gang-invite-window-timer = Temps restant : {$seconds}s
gang-invite-window-accept = Accepter
gang-invite-window-deny = Refuser

gang-creator-title = Créer Ton Gang
gang-creator-color-label = Choisis une couleur pour représenter ton gang :
gang-creator-confirm = Confirmer
gang-creator-too-dark = Cette couleur est trop transparente !
gang-creator-name-label = Choisis un nom pour ton gang :
gang-creator-name-placeholder = ex. Les Cobras
gang-creator-name-too-short = Le nom doit faire au moins {$min} caractères.
gang-creator-name-too-long = Le nom doit faire au plus {$max} caractères.
gang-creator-name-already-taken = Ce nom de gang est déjà pris !

gang-territory-too-close = Tu es trop près d'un territoire existant !
gang-locker-too-close-to-crate = Tu es trop près d'une caisse de gang existante !
gang-color-already-taken = Cette couleur a déjà été choisie !

gang-spray-can-verb-paint = Peindre au Spray
gang-spray-can-no-gang = Tu n'appartiens à aucun gang !
gang-spray-can-empty = Le spray est vide.
gang-spray-can-clothing-painted = Tu pulvérises la couleur de ton gang dessus.
gang-spray-can-points-earned = +{$points} Points de Gang !
gang-spray-can-charges-remaining = {$charges ->
    [one] Il lui reste [color=yellow]{$charges}[/color] utilisation.
    *[other] Il lui reste [color=yellow]{$charges}[/color] utilisations.
}

gang-store-title = Magasin de Gang
gang-store-in-use = Quelqu'un d'autre utilise déjà le casier !

store-currency-display-gangpoint = Points de Gang

gang-store-category-starter-equipment = Équipement de Débutant
gang-store-category-consumables = Consommables
gang-store-category-weapons = Armes
gang-store-category-equipment = Équipement

gang-store-listing-beginner-bundle-name = Pack Débutant
gang-store-listing-beginner-bundle-desc = Contient tout ce dont un aspirant gangster a besoin pour commencer. Hautement recommandé.

gang-store-listing-medkit-name = Trousse Médicale
gang-store-listing-medkit-desc = Une trousse médicale standard. Soigne-toi avant de retourner au combat.

gang-store-listing-omnizine-name = Omnizine
gang-store-listing-omnizine-desc = Une seringue d'omnizine. Soigne la plupart des types de dégâts rapidement.

gang-store-listing-quickhack-name = Quickhack
gang-store-listing-quickhack-desc = Un dispositif covert qui simule les signaux d'ouverture d'IA pour ouvrir les portes non verrouillées. Bon pour 5 utilisations.

gang-store-listing-knife-name = Couteau de Combat
gang-store-listing-knife-desc = Une lame fiable pour le combat rapproché. Rapide et mortelle.

gang-store-listing-machete-name = Machette de Gangster
gang-store-listing-machete-desc = Très efficace pour couper des membres.

gang-store-listing-throwingknife-name = Couteau de Lancer
gang-store-listing-throwingknife-desc = Un couteau équilibré fait pour le lancer. Bon pour éliminer des ennemis à distance.

gang-store-listing-baseballbat-name = Batte de Baseball
gang-store-listing-baseballbat-desc = Une arme de rue classique. Vise le visage.

gang-store-listing-propaint-name = Peinture en Spray
gang-store-listing-propaint-desc = Une bombe de peinture en spray de gang. Utilise-la pour revendiquer du territoire pour ton équipe.

gang-store-listing-clothes-bundle-name = Pack Vêtements de Gang
gang-store-listing-clothes-bundle-desc = Un ensemble de rechange de fringues de gang : chaussures, combinaison, veste et un chapeau haut-de-forme flashy. Porte tes couleurs pour gagner des bonus de territoire.

gangwar-crate-drop-announcement = On vient de recevoir l'info qu'une caisse de gang a atterri près de {$location}. Elle contient des trucs précieux. Tous les gangs de la station la veulent. Va la chercher.

gangwar-duffelbag-drop-announcement = On vient de recevoir l'info qu'un sac de sport a atterri près de {$location}. Tous les gangs de la station le veulent. Va le chercher.

gangwar-tipoff-drop-announcement = Quelqu'un a payé pour un tuyau. Un sac de sport vient d'atterrir près de {$location}. L'info est déjà dehors. Dépêche-toi.

gangwar-duffelbag-drop-unknown-location = un emplacement inconnu

gang-store-listing-light-armor-vest-name = Gilet Pare-balles Léger
gang-store-listing-light-armor-vest-desc = Un gilet pare-balles standard de Type I. Pas flashy, mais il te gardera en vie plus longtemps dans une bagarre.

gang-crate-no-locker-nearby = Tu dois être près d'un casier de gang pour ouvrir ça !

gang-crate-reward-popup = Ton gang a sécurisé la caisse ! +{$points} points de gang !

gang-duffel-bag-untrapped = Tu t'es coupé la main sur le piège du sac !
gang-duffel-bag-untrapping = Tu commences à désamorcer le piège..

alerts-gang-trapped-name = Piégé !
alerts-gang-trapped-desc = Le sac de sport était piégé ! Tu es maintenant ralenti.

gang-store-listing-encryption-key-name = Clé de Chiffrement de Gang
gang-store-listing-encryption-key-desc = Une clé de chiffrement accordée sur une fréquence spéciale utilisée par les gangs.

chat-radio-gang = Discussion de Gang

alerts-gang-bonus-name = Bonus de Territoire
alerts-gang-bonus-desc = Indique si ton bonus de territoire est actuellement actif.

gang-locker-examine-header = [color=#D2691E][bold]Classement des Gangs[/bold][/color]
gang-locker-examine-gang-entry = {$rank}. [color=#{$color}][bold]{$name}[/bold][/color] - {$score} pts
gang-locker-examine-update-timer = [italic]Mise à jour dans {$seconds}s[/italic]
gang-structure-revealed = La structure du gang se révèle.

gang-member-role-greeting =
    Tu as été admis dans le cercle.
    Ton chef a misé sur toi, alors ne le fais pas regretter.
    Étends ton territoire, couvre les arrières de ton gang, et aide à prendre le contrôle de cette station.
    Les gangs rivaux sont déjà dehors. Ne leur laisse pas un pouce.

objective-issuer-gang = Patron

gang-objective-recruit-title = Recruter {$count ->
    [one] {$count} membre
    *[other] {$count} membres
} pour ton gang
gang-objective-recruit-description = Utilise ton action d'offre pour faire entrer des soldats loyaux dans le cercle.

gang-objective-first-place-title = Être en première place
gang-objective-first-place-description = Assure que ton gang est en première place à la fin de la manche.

gang-objective-earn-points-title = Gagner {$count} points de gang
gang-objective-earn-points-description = Aide ton gang à accumuler des points.

gang-store-listing-tipoff-name = Tuyau
gang-store-listing-tipoff-desc = Programme un largage de sac de sport anticipé rempli de bonnes choses. Un civil au hasard sera informé de l'emplacement via NanoChat.

gangwar-tipoff-nanochat-sender = Anonyme
gangwar-tipoff-nanochat-message = Demande pas comment je sais ça, mais y'a un sac de sport planqué près de {$location}. Choppe-le avant les gangs. T'as {$seconds} secondes avant que l'info fuite.

quickhack-no-charges = Plus de charges restantes.
quickhack-failed = Échec de l'envoi du signal.