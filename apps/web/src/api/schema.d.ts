export interface paths {
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
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
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
        };
        CreateMatchTicketRequest: {
            ruleVersion: string;
            timeControl: null | string;
            clientRequestId: string;
        };
        CreateRoomRequest: {
            ruleVersion: string;
            timeControl: null | string;
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
        /** @enum {string} */
        GameResultReason: "generalCaptured" | "noLegalMove" | "resignation" | "timeout" | "agreedDraw" | "repetition" | "noProgress";
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
        };
        GuestSessionView: {
            /** Format: uuid */
            playerId: string;
            displayName: string;
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
        };
        MoveRequest: {
            from: components["schemas"]["Position"];
            to: components["schemas"]["Position"];
            /** Format: int64 */
            expectedVersion: number;
            clientMoveId: string;
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
        ReplayFrameView: {
            /** Format: int32 */
            ply: number;
            sideToMove: components["schemas"]["Side"];
            pieces: components["schemas"]["PieceView"][];
            move: null | components["schemas"]["ReplayMoveView"];
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
        ReplayView: {
            /** Format: uuid */
            gameId: string;
            ruleVersion: string;
            perspective: components["schemas"]["Side"];
            result: components["schemas"]["GameResultView"];
            frames: components["schemas"]["ReplayFrameView"][];
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
        };
        SetReadyRequest: {
            ready: boolean;
        };
        /** @enum {string} */
        Side: "red" | "black";
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
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
                    "text/plain": components["schemas"]["ReplayView"];
                    "application/json": components["schemas"]["ReplayView"];
                    "text/json": components["schemas"]["ReplayView"];
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
