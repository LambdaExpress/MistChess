export interface paths {
    "/api/admin/antiforgery/token": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getAdminAntiforgeryToken"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/admin/session": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getAdminSession"];
        put?: never;
        post: operations["createAdminSession"];
        delete: operations["deleteAdminSession"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/admin/users": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getAdminUsers"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/admin/users/{playerId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getAdminUser"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/admin/users/{playerId}/ban": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["banAdminUser"];
        delete: operations["unbanAdminUser"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/admin/users/{playerId}/games": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getAdminUserGames"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/admin/games/{gameId}/replay": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getAdminGameReplay"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/game-options": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getGameOptions"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/sessions/guest": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["createGuestSession"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/sessions/heartbeat": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["heartbeatGuestSession"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/antiforgery/token": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getAntiforgeryToken"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/rooms": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["createRoom"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/rooms/{code}/join": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["joinRoom"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/rooms/{code}/ready": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["setRoomReady"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/rooms/{code}/members/me": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        delete: operations["leaveRoom"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/matchmaking/tickets": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["createMatchTicket"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/matchmaking/tickets/current": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getCurrentMatchTicket"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/matchmaking/tickets/{ticketId}/heartbeat": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["heartbeatMatchTicket"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/matchmaking/tickets/{ticketId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        delete: operations["cancelMatchTicket"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/history": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getGameHistory"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{gameId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getGame"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{gameId}/moves": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["submitMove"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{gameId}/resign": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["resignGame"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{gameId}/draw-offers": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["offerDraw"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{gameId}/draw-offers/accept": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["acceptDraw"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{gameId}/draw-offers/reject": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["rejectDraw"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{gameId}/replay": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getReplay"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/games/{gameId}/replay-share": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["createReplayShare"];
        delete: operations["revokeReplayShare"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/api/replay-shares/{shareToken}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["getSharedReplay"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        AdminBanRequest: {
            reason: string;
        };
        AdminBanStatusView: {
            /** Format: uuid */
            playerId: string;
            banned: boolean;
            /** Format: date-time */
            bannedAt: null | string;
            banReason: null | string;
            bannedBy: null | string;
        };
        AdminHistoricalGamesPageView: {
            games: components["schemas"]["AdminHistoricalGameSummaryView"][];
            nextCursor: null | string;
        };
        AdminHistoricalGameSummaryView: {
            /** Format: uuid */
            gameId: string;
            /** Format: date-time */
            finishedAt: string;
            ruleVersion: string;
            timeControl: null | string;
            currentPlayerSide: components["schemas"]["Side"];
            red: components["schemas"]["HistoricalPlayerView"];
            black: components["schemas"]["HistoricalPlayerView"];
            result: components["schemas"]["GameResultView"];
            /** Format: int32 */
            plyCount: number;
            /** Format: int32 */
            moveTimeLimitSeconds: null | number;
            isRated: boolean;
        };
        AdminLoginRequest: {
            username: string;
            password: string;
        };
        AdminRatingView: {
            ruleVersion: string;
            timeControl: string;
            /** Format: int32 */
            rating: number;
            /** Format: int32 */
            gamesPlayed: number;
            /** Format: int32 */
            wins: number;
            /** Format: int32 */
            draws: number;
            /** Format: int32 */
            losses: number;
            /** Format: double */
            winRate: null | number;
            /** Format: date-time */
            updatedAt: string;
        };
        AdminSessionView: {
            username: string;
            /** Format: date-time */
            expiresAt: string;
        };
        AdminUserDetailView: {
            user: components["schemas"]["AdminUserSummaryView"];
            ratings: components["schemas"]["AdminRatingView"][];
            /** Format: date-time */
            observedAt: string;
        };
        AdminUsersPageView: {
            items: components["schemas"]["AdminUserSummaryView"][];
            nextCursor: null | string;
            /** Format: date-time */
            observedAt: string;
        };
        AdminUserSummaryView: {
            /** Format: uuid */
            playerId: string;
            displayName: string;
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            expiresAt: string;
            /** Format: date-time */
            lastSeenAt: string;
            online: boolean;
            banned: boolean;
            /** Format: date-time */
            bannedAt: null | string;
            banReason: null | string;
            bannedBy: null | string;
            /** Format: int32 */
            rating: number;
            /** Format: int32 */
            gamesPlayed: number;
            /** Format: int32 */
            wins: number;
            /** Format: int32 */
            draws: number;
            /** Format: int32 */
            losses: number;
            /** Format: double */
            winRate: null | number;
        };
        AntiforgeryTokenView: {
            token: string;
            headerName: string;
        };
        CandidateMoveView: {
            from: components["schemas"]["Position"];
            destinations: components["schemas"]["Position"][];
        };
        CaptureSummaryView: {
            redLost: components["schemas"]["PieceType"][];
            blackLost: components["schemas"]["PieceType"][];
        };
        ClockView: {
            /** Format: int64 */
            redMilliseconds: number;
            /** Format: int64 */
            blackMilliseconds: number;
            /** Format: date-time */
            serverTime: string;
            /** Format: int64 */
            turnMilliseconds?: null | number;
        };
        CreateMatchTicketRequest: {
            ruleVersion: string;
            clientRequestId: string;
        };
        CreateRoomRequest: {
            ruleVersion: string;
            timeControl: null | string;
            /** Format: int32 */
            moveTimeLimitSeconds?: null | number;
        };
        /** @enum {string} */
        DrawOfferStatus: "pending" | "accepted" | "rejected" | "withdrawn";
        DrawOfferView: {
            status: components["schemas"]["DrawOfferStatus"];
            offeredBy: components["schemas"]["Side"];
        };
        ErrorResponse: {
            code: string;
            title: string;
            detail?: null | string;
            /** Format: uuid */
            gameId?: null | string;
        };
        GameOptionsView: {
            ruleVersion: string;
            quickMatchTimeControl: components["schemas"]["TimeControlOptionView"];
            roomTimeControls: components["schemas"]["TimeControlOptionView"][];
            defaultRoomTimeControlId: string;
            allowUntimedRooms: boolean;
            /** Format: int32 */
            quickMatchMoveTimeLimitSeconds: number;
            roomMoveTimeLimits: components["schemas"]["MoveTimeLimitOptionView"][];
            /** Format: int32 */
            defaultRoomMoveTimeLimitSeconds: number;
        };
        /** @enum {string} */
        GameResultReason: "generalCaptured" | "noLegalMove" | "resignation" | "timeout" | "agreedDraw" | "repetition" | "noProgress" | "administrativeForfeit";
        GameResultView: {
            winner: null | components["schemas"]["Side"];
            reason: components["schemas"]["GameResultReason"];
        };
        /** @enum {string} */
        GameStatus: "waitingForOpponent" | "waitingForReady" | "playing" | "finished";
        GameView: {
            /** Format: uuid */
            gameId: string;
            ruleVersion: string;
            timeControl: null | string;
            /** Format: int64 */
            version: number;
            status: components["schemas"]["GameStatus"];
            result: null | components["schemas"]["GameResultView"];
            perspective: components["schemas"]["Side"];
            sideToMove: components["schemas"]["Side"];
            visibleSquares: components["schemas"]["Position"][];
            pieces: components["schemas"]["PieceView"][];
            candidateMoves: components["schemas"]["CandidateMoveView"][];
            captureSummary: components["schemas"]["CaptureSummaryView"];
            clock: null | components["schemas"]["ClockView"];
            drawOffer: null | components["schemas"]["DrawOfferView"];
            /** Format: int32 */
            moveTimeLimitSeconds?: null | number;
        };
        GuestSessionView: {
            /** Format: uuid */
            playerId: string;
            displayName: string;
            /** Format: uuid */
            activeGameId: null | string;
        };
        HistoricalGamesPageView: {
            games: components["schemas"]["HistoricalGameSummaryView"][];
            nextCursor: null | string;
        };
        HistoricalGameSummaryView: {
            /** Format: uuid */
            gameId: string;
            /** Format: date-time */
            finishedAt: string;
            ruleVersion: string;
            timeControl: null | string;
            currentPlayerSide: components["schemas"]["Side"];
            red: components["schemas"]["HistoricalPlayerView"];
            black: components["schemas"]["HistoricalPlayerView"];
            result: components["schemas"]["GameResultView"];
            /** Format: int32 */
            plyCount: number;
            /** Format: int32 */
            moveTimeLimitSeconds?: null | number;
        };
        /** @enum {string} */
        HistoricalOutcome: "win" | "loss" | "draw";
        HistoricalPlayerView: {
            displayName: string;
            outcome: components["schemas"]["HistoricalOutcome"];
        };
        HistoricalReplayFrameView: {
            /** Format: int32 */
            ply: number;
            sideToMove: components["schemas"]["Side"];
            clock: null | components["schemas"]["ClockView"];
            views: components["schemas"]["ReplayFrameViewsView"];
        };
        HistoricalReplayView: {
            /** Format: uuid */
            gameId: string;
            ruleVersion: string;
            timeControl: null | string;
            currentPlayerSide: null | components["schemas"]["Side"];
            red: components["schemas"]["HistoricalPlayerView"];
            black: components["schemas"]["HistoricalPlayerView"];
            result: components["schemas"]["GameResultView"];
            frames: components["schemas"]["HistoricalReplayFrameView"][];
            /** Format: int32 */
            moveTimeLimitSeconds?: null | number;
        };
        /** @enum {string} */
        MatchTicketStatus: "searching" | "matched" | "cancelled" | "expired";
        MatchTicketView: {
            /** Format: uuid */
            ticketId: string;
            ruleVersion: string;
            timeControl: null | string;
            status: components["schemas"]["MatchTicketStatus"];
            /** Format: date-time */
            createdAt: string;
            /** Format: date-time */
            lastHeartbeatAt: string;
            /** Format: date-time */
            expiresAt: string;
            /** Format: uuid */
            gameId: null | string;
            /** Format: int32 */
            moveTimeLimitSeconds?: null | number;
        };
        MoveRequest: {
            from: components["schemas"]["Position"];
            to: components["schemas"]["Position"];
            /** Format: int64 */
            expectedVersion: number;
            clientMoveId: string;
        };
        MoveTimeLimitOptionView: {
            /** Format: int32 */
            seconds: number;
            label: string;
        };
        /** @enum {string} */
        PieceType: "general" | "advisor" | "elephant" | "horse" | "rook" | "cannon" | "pawn";
        PieceView: {
            side: components["schemas"]["Side"];
            type: components["schemas"]["PieceType"];
            position: components["schemas"]["Position"];
        };
        Position: {
            /** Format: int32 */
            file: number;
            /** Format: int32 */
            rank: number;
        };
        ReplayFrameProjectionView: {
            visibleSquares: components["schemas"]["Position"][];
            pieces: components["schemas"]["PieceView"][];
            captureSummary: components["schemas"]["CaptureSummaryView"];
            move: null | components["schemas"]["ReplayMoveView"];
        };
        ReplayFrameViewsView: {
            red: components["schemas"]["ReplayFrameProjectionView"];
            black: components["schemas"]["ReplayFrameProjectionView"];
            omniscient: components["schemas"]["ReplayFrameProjectionView"];
        };
        ReplayMoveView: {
            /** Format: int32 */
            ply: number;
            side: components["schemas"]["Side"];
            piece: components["schemas"]["PieceType"];
            from: components["schemas"]["Position"];
            to: components["schemas"]["Position"];
            captured: null | components["schemas"]["PieceType"];
        };
        ReplayShareCreatedView: {
            sharePath: string;
            /** Format: date-time */
            createdAt: string;
        };
        RoomPlayerView: {
            displayName: string;
            side: null | components["schemas"]["Side"];
            isReady: boolean;
            isCurrentPlayer: boolean;
        };
        RoomView: {
            code: string;
            status: components["schemas"]["GameStatus"];
            ruleVersion: string;
            timeControl: null | string;
            players: components["schemas"]["RoomPlayerView"][];
            /** Format: uuid */
            gameId: null | string;
            /** Format: int32 */
            moveTimeLimitSeconds?: null | number;
        };
        SetReadyRequest: {
            ready: boolean;
        };
        /** @enum {string} */
        Side: "red" | "black";
        TimeControlOptionView: {
            id: string;
            label: string;
            /** Format: int32 */
            initialSeconds: number;
            /** Format: int32 */
            incrementSeconds: number;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
    getAdminAntiforgeryToken: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["AntiforgeryTokenView"];
                    "application/json": components["schemas"]["AntiforgeryTokenView"];
                    "text/json": components["schemas"]["AntiforgeryTokenView"];
                };
            };
        };
    };
    getAdminSession: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["AdminSessionView"];
                    "application/json": components["schemas"]["AdminSessionView"];
                    "text/json": components["schemas"]["AdminSessionView"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    createAdminSession: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AdminLoginRequest"];
                "text/json": components["schemas"]["AdminLoginRequest"];
                "application/*+json": components["schemas"]["AdminLoginRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["AdminSessionView"];
                    "application/json": components["schemas"]["AdminSessionView"];
                    "text/json": components["schemas"]["AdminSessionView"];
                };
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
            /** @description Too Many Requests */
            429: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
            /** @description Service Unavailable */
            503: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    deleteAdminSession: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    getAdminUsers: {
        parameters: {
            query?: {
                query?: string;
                status?: string;
                online?: string;
                cursor?: string;
                limit?: number;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["AdminUsersPageView"];
                    "application/json": components["schemas"]["AdminUsersPageView"];
                    "text/json": components["schemas"]["AdminUsersPageView"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    getAdminUser: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                playerId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["AdminUserDetailView"];
                    "application/json": components["schemas"]["AdminUserDetailView"];
                    "text/json": components["schemas"]["AdminUserDetailView"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    banAdminUser: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                playerId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AdminBanRequest"];
                "text/json": components["schemas"]["AdminBanRequest"];
                "application/*+json": components["schemas"]["AdminBanRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["AdminBanStatusView"];
                    "application/json": components["schemas"]["AdminBanStatusView"];
                    "text/json": components["schemas"]["AdminBanStatusView"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    unbanAdminUser: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                playerId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["AdminBanStatusView"];
                    "application/json": components["schemas"]["AdminBanStatusView"];
                    "text/json": components["schemas"]["AdminBanStatusView"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    getAdminUserGames: {
        parameters: {
            query?: {
                cursor?: string;
                limit?: number;
                ruleVersion?: string;
                timeControl?: string;
                result?: string;
            };
            header?: never;
            path: {
                playerId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["AdminHistoricalGamesPageView"];
                    "application/json": components["schemas"]["AdminHistoricalGamesPageView"];
                    "text/json": components["schemas"]["AdminHistoricalGamesPageView"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    getAdminGameReplay: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                gameId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["HistoricalReplayView"];
                    "application/json": components["schemas"]["HistoricalReplayView"];
                    "text/json": components["schemas"]["HistoricalReplayView"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    getGameOptions: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["GameOptionsView"];
                    "application/json": components["schemas"]["GameOptionsView"];
                    "text/json": components["schemas"]["GameOptionsView"];
                };
            };
        };
    };
    createGuestSession: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["GuestSessionView"];
                    "application/json": components["schemas"]["GuestSessionView"];
                    "text/json": components["schemas"]["GuestSessionView"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    heartbeatGuestSession: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Unauthorized */
            401: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
            /** @description Forbidden */
            403: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    getAntiforgeryToken: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["AntiforgeryTokenView"];
                    "application/json": components["schemas"]["AntiforgeryTokenView"];
                    "text/json": components["schemas"]["AntiforgeryTokenView"];
                };
            };
        };
    };
    createRoom: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CreateRoomRequest"];
                "text/json": components["schemas"]["CreateRoomRequest"];
                "application/*+json": components["schemas"]["CreateRoomRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["RoomView"];
                    "application/json": components["schemas"]["RoomView"];
                    "text/json": components["schemas"]["RoomView"];
                };
            };
        };
    };
    joinRoom: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                code: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["RoomView"];
                    "application/json": components["schemas"]["RoomView"];
                    "text/json": components["schemas"]["RoomView"];
                };
            };
        };
    };
    setRoomReady: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                code: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SetReadyRequest"];
                "text/json": components["schemas"]["SetReadyRequest"];
                "application/*+json": components["schemas"]["SetReadyRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["RoomView"];
                    "application/json": components["schemas"]["RoomView"];
                    "text/json": components["schemas"]["RoomView"];
                };
            };
        };
    };
    leaveRoom: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                code: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    createMatchTicket: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CreateMatchTicketRequest"];
                "text/json": components["schemas"]["CreateMatchTicketRequest"];
                "application/*+json": components["schemas"]["CreateMatchTicketRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["MatchTicketView"];
                    "application/json": components["schemas"]["MatchTicketView"];
                    "text/json": components["schemas"]["MatchTicketView"];
                };
            };
        };
    };
    getCurrentMatchTicket: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["MatchTicketView"];
                    "application/json": components["schemas"]["MatchTicketView"];
                    "text/json": components["schemas"]["MatchTicketView"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    heartbeatMatchTicket: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                ticketId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["MatchTicketView"];
                    "application/json": components["schemas"]["MatchTicketView"];
                    "text/json": components["schemas"]["MatchTicketView"];
                };
            };
        };
    };
    cancelMatchTicket: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                ticketId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["MatchTicketView"];
                    "application/json": components["schemas"]["MatchTicketView"];
                    "text/json": components["schemas"]["MatchTicketView"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    getGameHistory: {
        parameters: {
            query?: {
                cursor?: string;
                limit?: number;
                ruleVersion?: string;
                timeControl?: string;
                result?: string;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["HistoricalGamesPageView"];
                    "application/json": components["schemas"]["HistoricalGamesPageView"];
                    "text/json": components["schemas"]["HistoricalGamesPageView"];
                };
            };
        };
    };
    getGame: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                gameId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["GameView"];
                    "application/json": components["schemas"]["GameView"];
                    "text/json": components["schemas"]["GameView"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    submitMove: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                gameId: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["MoveRequest"];
                "text/json": components["schemas"]["MoveRequest"];
                "application/*+json": components["schemas"]["MoveRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["GameView"];
                    "application/json": components["schemas"]["GameView"];
                    "text/json": components["schemas"]["GameView"];
                };
            };
            /** @description Conflict */
            409: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
            /** @description Unprocessable Entity */
            422: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    resignGame: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                gameId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["GameView"];
                    "application/json": components["schemas"]["GameView"];
                    "text/json": components["schemas"]["GameView"];
                };
            };
        };
    };
    offerDraw: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                gameId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["DrawOfferView"];
                    "application/json": components["schemas"]["DrawOfferView"];
                    "text/json": components["schemas"]["DrawOfferView"];
                };
            };
        };
    };
    acceptDraw: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                gameId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["GameView"];
                    "application/json": components["schemas"]["GameView"];
                    "text/json": components["schemas"]["GameView"];
                };
            };
        };
    };
    rejectDraw: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                gameId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["DrawOfferView"];
                    "application/json": components["schemas"]["DrawOfferView"];
                    "text/json": components["schemas"]["DrawOfferView"];
                };
            };
        };
    };
    getReplay: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                gameId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["HistoricalReplayView"];
                    "application/json": components["schemas"]["HistoricalReplayView"];
                    "text/json": components["schemas"]["HistoricalReplayView"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    createReplayShare: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                gameId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ReplayShareCreatedView"];
                    "application/json": components["schemas"]["ReplayShareCreatedView"];
                    "text/json": components["schemas"]["ReplayShareCreatedView"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    revokeReplayShare: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                gameId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
    getSharedReplay: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                shareToken: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["HistoricalReplayView"];
                    "application/json": components["schemas"]["HistoricalReplayView"];
                    "text/json": components["schemas"]["HistoricalReplayView"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/plain": components["schemas"]["ErrorResponse"];
                    "application/json": components["schemas"]["ErrorResponse"];
                    "text/json": components["schemas"]["ErrorResponse"];
                };
            };
        };
    };
}
